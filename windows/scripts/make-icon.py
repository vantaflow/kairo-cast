import struct, zlib, math, sys
def render(n):
    ss=3; N=n*ss
    px=[[0,0,0,0] for _ in range(n*n)]
    def inrr(x,y,x0,y0,x1,y1,r):
        if x<x0 or x>x1 or y<y0 or y>y1: return False
        cx=min(max(x,x0+r),x1-r); cy=min(max(y,y0+r),y1-r)
        return (x-cx)**2+(y-cy)**2<=r*r
    def cover(x,y):
        # returns (bg, fg)
        bg=inrr(x,y,0.02,0.02,0.98,0.98,0.22)
        fg=False
        if inrr(x,y,0.17,0.25,0.83,0.75,0.06) and not inrr(x,y,0.24,0.32,0.76,0.68,0.03): fg=True
        cx,cy=0.31,0.60
        d=math.hypot(x-cx,y-cy)
        if d<0.045: fg=True
        for r in (0.12,0.21):
            if abs(d-r)<0.026 and x>=cx-0.001 and y<=cy+0.001 and x<0.76 and y>0.32: fg=True
        return bg,fg
    for j in range(n):
        for i in range(n):
            bgc=fgc=0
            for sj in range(ss):
                for si in range(ss):
                    x=(i*ss+si+0.5)/N; y=(j*ss+sj+0.5)/N
                    b,f=cover(x,y)
                    bgc+=b and not f; fgc+=f and b
            t=ss*ss
            a=(bgc+fgc)/t
            if a==0: continue
            fr=fgc/(bgc+fgc)
            r=int(0x25*(1-fr)+255*fr); g=int(0x63*(1-fr)+255*fr); bl=int(0xEB*(1-fr)+255*fr)
            px[j*n+i]=[r,g,bl,int(255*a)]
    return px
def png(n):
    px=render(n)
    raw=b''.join(b'\x00'+bytes(sum((px[j*n+i] for i in range(n)),[])) for j in range(n))
    def ch(t,d): 
        c=struct.pack('>I',len(d))+t+d
        return c+struct.pack('>I',zlib.crc32(t+d)&0xffffffff)
    return b'\x89PNG\r\n\x1a\n'+ch(b'IHDR',struct.pack('>IIBBBBB',n,n,8,6,0,0,0))+ch(b'IDAT',zlib.compress(raw,9))+ch(b'IEND',b'')
sizes=[16,32,48,256]
imgs=[png(s) for s in sizes]
out=struct.pack('<HHH',0,1,len(sizes)); off=6+16*len(sizes)
for s,d in zip(sizes,imgs):
    out+=struct.pack('<BBBBHHII',0 if s==256 else s,0 if s==256 else s,0,0,1,32,len(d),off); off+=len(d)
out+=b''.join(imgs)
open(sys.argv[1],'wb').write(out)
