"""Genere les vecteurs de test du protocole Kairo Link (une seule fois, binaires versionnes)."""
import json
import os
import struct

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "vectors")
os.makedirs(OUT, exist_ok=True)


def frame(t, flags, pts, payload):
    return b"KCST" + struct.pack(">BBHqI", t, flags, 0, pts, len(payload)) + payload


def write(name, data):
    with open(os.path.join(OUT, name), "wb") as f:
        f.write(data)


hello = json.dumps(
    {"v": 1, "name": "PC-Salon", "w": 1920, "h": 1080, "fps": 30, "audio": "pcm_s16le_48000_2"},
    separators=(",", ":"),
).encode()
video = frame(2, 1, 123456, bytes([0, 0, 0, 1, 0x67, 0x42, 0x00, 0x1F]))
write("hello.bin", frame(1, 0, 0, hello))
write("video_key.bin", video)
write("audio.bin", frame(3, 0, 1000, bytes([1, 2]) * 960))
write("ping.bin", frame(6, 0, 0, b""))
write("keyreq.bin", frame(5, 0, 0, b""))
write("bye.bin", frame(4, 0, 0, b""))
write("truncated.bin", video[:20])
write("badmagic.bin", b"XCST" + video[4:])
write("huge.bin", b"KCST" + struct.pack(">BBHqI", 2, 0, 0, 0, 0xFFFFFFFF))
