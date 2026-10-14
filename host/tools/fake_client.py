"""Cliente de prueba que simula la tablet: hace el handshake, recibe video y lo guarda.

Uso: python tools/fake_client.py [--host 127.0.0.1] [--port 27190] [--seconds 5] [--out capture.h264]
                                 [--pen-hover] [--pin 123456] [--identity <carpeta>]
                                 [--request-keyframe <s>] [--limit-mbps <n> [--limit-seconds <s>]]
El archivo resultante se puede validar con: ffprobe capture.h264
--pen-hover acerca un lápiz virtual al centro del monitor y lo aleja (mueve el cursor, no hace clic).
--request-keyframe <s> pide un keyframe a los s segundos, como la app cuando falla el decoder.
--limit-mbps <n> lee como mucho n Mbps, para simular un WiFi lento (el host debería bajar el bitrate).
Con --limit-seconds <s> el límite dura s segundos y después la red queda libre (el host debería volver a subir).

Con --host 127.0.0.1 se comporta como una conexión USB (sin cifrar). Con otra IP usa TLS como la
app por WiFi (requiere el paquete "cryptography"): la primera vez el host pide el PIN, que se pasa
con --pin o se escribe cuando lo pide. El certificado y los PCs de confianza se guardan en
--identity (por defecto ~/.nexscreen-fake-client) para poder probar la reconexión sin PIN.
"""

import argparse
import datetime
import hashlib
import hmac
import json
import socket
import ssl
import struct
import time
from pathlib import Path

HELLO, CONFIG, PAIR_REQUIRED, PAIR, PAIR_RESULT = 0x01, 0x02, 0x03, 0x04, 0x05
VIDEO, REQUEST_KEYFRAME, POINTER, PING, PONG = 0x10, 0x11, 0x20, 0x30, 0x31
PROTOCOL = 2
TOOL_PEN, ACTION_HOVER, ACTION_HOVER_EXIT = 1, 4, 5


def read_exact(sock: socket.socket, size: int) -> bytes:
    data = bytearray()
    while len(data) < size:
        chunk = sock.recv(size - len(data))
        if not chunk:
            raise ConnectionError("conexión cerrada")
        data.extend(chunk)
    return bytes(data)


def send(sock: socket.socket, msg_type: int, payload: bytes) -> None:
    sock.sendall(struct.pack(">BI", msg_type, len(payload)) + payload)


def pairing_proof(pin: str, host_fp: str, client_fp: str) -> str:
    """Igual que Pairing.ComputeProof (host) y Pairing.proof (Android)."""
    message = f"NexScreen pairing v2\n{host_fp}\n{client_fp}".encode()
    return hmac.new(pin.encode(), message, hashlib.sha256).hexdigest()


def fingerprint(der: bytes) -> str:
    return hashlib.sha256(der).hexdigest()


def client_identity(folder: Path) -> tuple[Path, Path]:
    """Certificado autofirmado de la "tablet" (como el del Android Keystore)."""
    cert_path, key_path = folder / "client.crt", folder / "client.key"
    if cert_path.exists() and key_path.exists():
        return cert_path, key_path

    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID

    folder.mkdir(parents=True, exist_ok=True)
    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "NexScreen fake client")])
    now = datetime.datetime.now(datetime.timezone.utc)
    cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name).public_key(key.public_key())
            .serial_number(x509.random_serial_number())
            .not_valid_before(now - datetime.timedelta(days=1)).not_valid_after(now + datetime.timedelta(days=3650))
            .sign(key, hashes.SHA256()))
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    key_path.write_bytes(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                           serialization.NoEncryption()))
    return cert_path, key_path


def pointer(pid: int, tool: int, action: int, x: float, y: float, pressure: float = 0.0) -> bytes:
    """Mensaje POINTER con una sola entrada (ver docs/protocol.md)."""
    to16 = lambda v: round(min(max(v, 0.0), 1.0) * 65535)
    return struct.pack(">B", 1) + struct.pack(">BBBBHHHbb", pid, tool, action, 0, to16(x), to16(y), to16(pressure), 0, 0)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=27190)
    parser.add_argument("--seconds", type=float, default=5)
    parser.add_argument("--out", default="capture.h264")
    parser.add_argument("--pen-hover", action="store_true", help="probar la inyección de lápiz sin hacer clic")
    parser.add_argument("--pin", help="PIN de emparejamiento (por WiFi); si falta, se pregunta")
    parser.add_argument("--identity", type=Path, default=Path.home() / ".nexscreen-fake-client")
    parser.add_argument("--request-keyframe", type=float, metavar="S", help="pedir un keyframe a los S segundos")
    parser.add_argument("--limit-mbps", type=float, help="leer como mucho N Mbps (simula un WiFi lento)")
    parser.add_argument("--limit-seconds", type=float, help="quitar el límite de --limit-mbps a los S segundos")
    args = parser.parse_args()

    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    if args.limit_mbps:
        # Buffer chico: así la ventana de TCP se llena enseguida y el host lo nota.
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 64 * 1024)
    sock.settimeout(10)
    sock.connect((args.host, args.port))
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    usb = args.host == "127.0.0.1"
    host_fp = client_fp = None
    trusted_path = args.identity / "trusted-hosts.json"
    trusted = {}
    if not usb:
        cert_path, key_path = client_identity(args.identity)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        # Certificado autofirmado: la confianza sale del emparejamiento (huella), no de una CA.
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        context.load_cert_chain(cert_path, key_path)
        sock = context.wrap_socket(sock)
        host_fp = fingerprint(sock.getpeercert(binary_form=True))
        client_fp = fingerprint(ssl.PEM_cert_to_DER_cert(cert_path.read_text()))
        trusted = json.loads(trusted_path.read_text()) if trusted_path.exists() else {}
        print(f"TLS {sock.version()}, huella del host {host_fp[:16]}…, "
              f"{'de confianza' if host_fp in trusted else 'desconocido'}")

    hello = {"protocol": PROTOCOL, "device": "fake-client", "width": 1440, "height": 2304, "dpi": 274,
             "paired": host_fp in trusted}
    send(sock, HELLO, json.dumps(hello).encode())

    frames = keyframes = total = 0
    rtts = []
    start = time.monotonic()
    last_ping = 0.0
    hover_step = 0
    with open(args.out, "wb") as out:
        while time.monotonic() - start < args.seconds:
            elapsed = time.monotonic() - start
            if args.pen_hover and elapsed > 2 and hover_step < 20:
                # Recorre horizontalmente el centro del monitor durante ~1 s y luego se aleja.
                send(sock, POINTER, pointer(0, TOOL_PEN, ACTION_HOVER, 0.4 + hover_step * 0.01, 0.5))
                hover_step += 1
                if hover_step == 20:
                    print("lápiz en (0.59, 0.5) del monitor virtual")
            elif args.pen_hover and hover_step == 20 and elapsed > args.seconds - 1:
                send(sock, POINTER, pointer(0, TOOL_PEN, ACTION_HOVER_EXIT, 0.59, 0.5))
                hover_step += 1
            if args.request_keyframe is not None and elapsed > args.request_keyframe:
                send(sock, REQUEST_KEYFRAME, b"")
                print(f"t={elapsed:.2f}s: pedido de keyframe")
                args.request_keyframe = None
            if args.limit_mbps and args.limit_seconds is not None and elapsed > args.limit_seconds:
                print(f"t={elapsed:.2f}s: sin límite de lectura")
                args.limit_mbps = None
            if args.limit_mbps:
                ahead = total * 8 / (args.limit_mbps * 1e6) - elapsed
                if ahead > 0:
                    time.sleep(ahead)
            if time.monotonic() - last_ping > 1:
                send(sock, PING, struct.pack(">q", time.monotonic_ns() // 1000))
                last_ping = time.monotonic()

            msg_type, length = struct.unpack(">BI", read_exact(sock, 5))
            payload = read_exact(sock, length)
            if msg_type == PAIR_REQUIRED:
                host_name = json.loads(payload)["host"]
                pin = args.pin or input(f"PIN que muestra {host_name}: ").strip()
                args.pin = None  # si es incorrecto, el siguiente se pregunta
                send(sock, PAIR, json.dumps({"proof": pairing_proof(pin, host_fp, client_fp)}).encode())
                start = time.monotonic()
            elif msg_type == PAIR_RESULT:
                result = json.loads(payload)
                print("PAIR_RESULT", result)
                if result["ok"]:
                    trusted[host_fp] = host_name
                    trusted_path.write_text(json.dumps(trusted, indent=2))
                elif result.get("retry"):
                    pin = input(f"{result.get('error')} Otro PIN: ").strip()
                    send(sock, PAIR, json.dumps({"proof": pairing_proof(pin, host_fp, client_fp)}).encode())
                else:
                    raise SystemExit(f"Emparejamiento rechazado: {result.get('error')}")
                start = time.monotonic()
            elif msg_type == CONFIG:
                print("CONFIG", json.loads(payload))
            elif msg_type == VIDEO:
                pts, flags = struct.unpack(">qB", payload[:9])
                out.write(payload[9:])
                total += length
                if flags & 2:
                    print(f"t={time.monotonic() - start:.2f}s: config frame ({length - 9} bytes)")
                    continue
                frames += 1
                keyframes += flags & 1
            elif msg_type == PONG:
                (sent,) = struct.unpack(">q", payload)
                rtts.append((time.monotonic_ns() // 1000 - sent) / 1000)

    elapsed = time.monotonic() - start
    print(f"{frames} frames ({keyframes} keyframes) en {elapsed:.1f}s = {frames / elapsed:.1f} fps, "
          f"{total * 8 / elapsed / 1e6:.2f} Mbps")
    if rtts:
        print(f"RTT ping: min {min(rtts):.2f} ms, max {max(rtts):.2f} ms")


if __name__ == "__main__":
    main()
