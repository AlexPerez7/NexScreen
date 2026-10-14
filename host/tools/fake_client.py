"""Cliente de prueba que simula la tablet: hace el handshake, recibe video y lo guarda.

Uso: python tools/fake_client.py [--host 127.0.0.1] [--port 27190] [--seconds 5] [--out capture.h264]
                                 [--pen-hover]
El archivo resultante se puede validar con: ffprobe capture.h264
--pen-hover acerca un lápiz virtual al centro del monitor y lo aleja (mueve el cursor, no hace clic).
"""

import argparse
import json
import socket
import struct
import time

HELLO, CONFIG, VIDEO, POINTER, PING, PONG = 0x01, 0x02, 0x10, 0x20, 0x30, 0x31
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
    args = parser.parse_args()

    sock = socket.create_connection((args.host, args.port), timeout=10)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    hello = {"protocol": 1, "device": "fake-client", "width": 1440, "height": 2304, "dpi": 274}
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
