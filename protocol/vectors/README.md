# Vecteurs Kairo Link

Generes par `protocol/gen_vectors.py`, lus par les tests Kotlin et C#.
En-tete : "KCST" | type u8 | flags u8 | reserve u16 | pts_us i64 | len u32 (big-endian, 20 octets).

- hello.bin : HELLO, JSON `{"v":1,"name":"PC-Salon","w":1920,"h":1080,"fps":30,"audio":"pcm_s16le_48000_2"}`
- video_key.bin : VIDEO, flags 1 (cle), pts 123456, charge `00 00 00 01 67 42 00 1f`
- audio.bin : AUDIO, pts 1000, 1920 octets (01 02 repetes)
- ping.bin, keyreq.bin, bye.bin : trames sans charge (types 6, 5, 4)
- truncated.bin : en-tete de video_key.bin seul (charge manquante) -> erreur
- badmagic.bin : magic "XCST" -> erreur
- huge.bin : len = 0xFFFFFFFF -> erreur sans allocation
