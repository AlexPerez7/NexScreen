"""Cliente de prueba que simula la tablet: hace el handshake, recibe video y lo guarda.

Uso: python tools/fake_client.py [--host 127.0.0.1] [--port 27190] [--seconds 5] [--out capture.h264]
El archivo resultante se puede validar con: ffprobe capture.h264
"""

import argparse
import json
import socket
import struct
import time

HELLO, CONFIG, VIDEO, PING, PONG = 0x01, 0x02, 0x10, 0x30, 0x31


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


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=27190)
    parser.add_argument("--seconds", type=float, default=5)
    parser.add_argument("--out", default="capture.h264")
    args = parser.parse_args()

    sock = socket.create_connection((args.host, args.port), timeout=10)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello = {"protocol": 1, "device": "fake-client", "width": 1440, "height": 2304, "dpi": 274}
    send(sock, HELLO, json.dumps(hello).encode())

    frames = keyframes = total = 0
    rtts = []
    start = time.monotonic()
    last_ping = 0.0
    with open(args.out, "wb") as out:
        while time.monotonic() - start < args.seconds:
            if time.monotonic() - last_ping > 1:
                send(sock, PING, struct.pack(">q", time.monotonic_ns() // 1000))
                last_ping = time.monotonic()

            msg_type, length = struct.unpack(">BI", read_exact(sock, 5))
            payload = read_exact(sock, length)
            if msg_type == CONFIG:
                print("CONFIG", json.loads(payload))
            elif msg_type == VIDEO:
                pts, flags = struct.unpack(">qB", payload[:9])
                out.write(payload[9:])
                total += length
                if flags & 2:
                    print(f"config frame ({length - 9} bytes)")
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
