#!/usr/bin/env python3
"""Faux émetteur Kairo Link (tests sur la box sans PC Windows).

Usage : python3 -I tools/fake_sender.py <ip> <fichier.h264> [--port 7300] [--name PC-Test]
        [--width 1920 --height 1080 --fps 30] [--once] [--no-audio] [--seconds N]

Envoie HELLO, la vidéo Annex-B découpée en unités d'accès à <fps> i/s (en boucle sauf --once),
une tonalité PCM de 440 Hz (s16le 48 kHz stéréo), des PING toutes les 2 s, et répond à KEYREQ
en renvoyant la dernière IDR. Stdlib uniquement.
"""
import argparse
import json
import math
import socket
import struct
import sys
import threading
import time

MAGIC = b"KCST"
HELLO, VIDEO, AUDIO, BYE, KEYREQ, PING = 1, 2, 3, 4, 5, 6
HEADER = 20


def frame(ftype, flags, pts_us, payload=b""):
    return MAGIC + struct.pack(">BBHqI", ftype, flags, 0, pts_us, len(payload)) + payload


def read_exact(sock, n):
    buf = b""
    while len(buf) < n:
        chunk = sock.recv(n - len(buf))
        if not chunk:
            raise EOFError
        buf += chunk
    return buf


def read_frame(sock):
    h = read_exact(sock, HEADER)
    if h[:4] != MAGIC:
        raise ValueError("magic invalide")
    ftype, flags, _, pts, ln = struct.unpack(">BBHqI", h[4:])
    return ftype, flags, pts, read_exact(sock, ln) if ln else b""


def nal_units(data):
    """Renvoie la liste des NAL (start code inclus)."""
    starts = []
    i = 0
    n = len(data)
    while i + 3 <= n:
        if data[i] == 0 and data[i + 1] == 0 and data[i + 2] == 1:
            starts.append(i - 1 if i > 0 and data[i - 1] == 0 else i)
            i += 3
        else:
            i += 1
    starts.append(n)
    return [data[starts[k]:starts[k + 1]] for k in range(len(starts) - 1)]


def nal_type_and_first(nal):
    off = 4 if nal[:4] == b"\x00\x00\x00\x01" else 3
    t = nal[off] & 0x1F
    first_mb0 = len(nal) > off + 1 and (nal[off + 1] & 0x80) != 0
    return t, first_mb0


def access_units(data):
    """Regroupe les NAL en unités d'accès : [(octets, est_clé)]."""
    aus, cur, has_vcl, key = [], [], False, False
    for nal in nal_units(data):
        t, first = nal_type_and_first(nal)
        new_au = (t in (6, 7, 8, 9) and has_vcl) or (t in (1, 5) and has_vcl and first)
        if new_au and cur:
            aus.append((b"".join(cur), key))
            cur, has_vcl, key = [], False, False
        cur.append(nal)
        if t in (1, 5):
            has_vcl = True
        if t in (5, 7):
            key = True
    if cur:
        aus.append((b"".join(cur), key))
    return aus


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("ip")
    ap.add_argument("file")
    ap.add_argument("--port", type=int, default=7300)
    ap.add_argument("--name", default="PC-Test")
    ap.add_argument("--width", type=int, default=1920)
    ap.add_argument("--height", type=int, default=1080)
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--no-audio", action="store_true")
    ap.add_argument("--seconds", type=float, default=0, help="durée max (0 = illimitée)")
    a = ap.parse_args()

    with open(a.file, "rb") as f:
        aus = access_units(f.read())
    if not aus or not any(k for _, k in aus):
        sys.exit("aucune IDR dans le fichier")
    print(f"{len(aus)} unités d'accès, {sum(1 for _, k in aus if k)} clé(s)")

    sock = socket.create_connection((a.ip, a.port), timeout=5)
    sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    sock.settimeout(None)
    wlock = threading.Lock()
    stop = threading.Event()
    last_key = [None]
    keyreq = threading.Event()

    def send(data):
        with wlock:
            sock.sendall(data)

    hello = {"v": 1, "name": a.name, "w": a.width, "h": a.height, "fps": a.fps, "audio": "pcm_s16le_48000_2"}
    send(frame(HELLO, 0, 0, json.dumps(hello).encode()))
    print("HELLO envoyé")
    t0 = time.monotonic()

    def now_us():
        return int((time.monotonic() - t0) * 1e6)

    def reader():
        try:
            while not stop.is_set():
                ftype, _, _, _ = read_frame(sock)
                if ftype == KEYREQ:
                    print("KEYREQ reçu")
                    keyreq.set()
                elif ftype == BYE:
                    print("BYE reçu")
                    break
        except Exception as e:
            print("lecture terminée :", e)
        stop.set()

    def pinger():
        while not stop.wait(2.0):
            try:
                send(frame(PING, 0, now_us()))
            except OSError:
                stop.set()

    def audio():
        rate, chunk = 48000, 960  # 20 ms
        phase = 0
        nxt = time.monotonic()
        while not stop.is_set():
            buf = bytearray()
            for _ in range(chunk):
                v = int(8000 * math.sin(2 * math.pi * 440 * phase / rate))
                phase += 1
                buf += struct.pack("<hh", v, v)
            try:
                send(frame(AUDIO, 0, now_us(), bytes(buf)))
            except OSError:
                stop.set()
                return
            nxt += chunk / rate
            time.sleep(max(0, nxt - time.monotonic()))

    for fn in (reader, pinger) + (() if a.no_audio else (audio,)):
        threading.Thread(target=fn, daemon=True).start()

    period = 1.0 / a.fps
    nxt = time.monotonic()
    idx = 0
    sent = 0
    try:
        while not stop.is_set():
            if a.seconds and time.monotonic() - t0 > a.seconds:
                break
            if idx >= len(aus):
                if a.once:
                    break
                idx = 0
            au, key = aus[idx]
            idx += 1
            if key:
                last_key[0] = au
            try:
                if keyreq.is_set() and last_key[0] is not None:
                    keyreq.clear()
                    send(frame(VIDEO, 1, now_us(), last_key[0]))
                    print("IDR renvoyée")
                send(frame(VIDEO, 1 if key else 0, now_us(), au))
            except OSError as e:
                print("envoi impossible :", e)
                break
            sent += 1
            if sent % (a.fps * 5) == 0:
                print(f"{sent} images envoyées")
            nxt += period
            time.sleep(max(0, nxt - time.monotonic()))
    except KeyboardInterrupt:
        pass
    finally:
        stop.set()
        try:
            send(frame(BYE, 0, now_us()))
        except OSError:
            pass
        sock.close()
    print("terminé")


if __name__ == "__main__":
    main()
