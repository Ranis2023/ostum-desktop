"""Render Sfera's small looping 3D flames. Run manually when the artwork changes."""
import argparse
from pathlib import Path
import numpy as np
from PIL import Image

WIDTH, HEIGHT, FRAMES, COLUMNS = 78, 87, 48, 12
# Row order is shared with AgentLight.cs. The body has its own warm luminous core.
PALETTES = ['8057db','6253dc','12a88c','dc912e','d6579b','2c99da','ef634c','667bcc','be733d','a853bf','57a753']


def rgb(value):
    return np.frombuffer(bytes.fromhex(value),dtype=np.uint8).astype(float)/255


def mix(a,b,k):
    return a+(b-a)*np.asarray(k)[...,None]


def unit(v):
    return v/np.maximum(1e-8,np.linalg.norm(v,axis=-1,keepdims=True))


def over(layer,color,alpha):
    amount=np.clip(alpha,0,1)[...,None]
    layer[:,:,:3]=color*amount+layer[:,:,:3]*(1-amount)
    layer[:,:,3:4]=amount+layer[:,:,3:4]*(1-amount)


def render(t,paint):
    # Orthographic camera, x to the right, z toward the viewer. Each tongue is a
    # swept solid with twisting elliptical cross-sections. Resolve the front
    # intersection of every ray and shade its geometric normal in camera space.
    xs=np.linspace(-1,1,WIDTH);ys=np.linspace(1.76,-.27,HEIGHT)
    x,y=np.meshgrid(xs,ys);layer=np.zeros((HEIGHT,WIDTH,4))
    base_shadow=np.exp(-(x/.40)**2-((y+.025)/.075)**2)
    over(layer,rgb('665566'),base_shadow*.22)
    halo=np.exp(-1.8*((x/.64)**2+((y-.49)/.80)**2))
    over(layer,mix(paint,np.ones(3),.52),halo*.16)
    view=.17*np.sin(t);twist=.85*np.sin(t+.35)
    light=unit(np.array([-.53,.72,.68]));half=unit(light+np.array([0.,0.,1.]))
    depths=np.full(x.shape,-100.);colors=np.zeros(x.shape+(3,))

    def tongue(bottom,top,radius,cx,depth,phase,profile,material):
        h=np.clip((y-bottom)/(top-bottom),0,1)
        swell=(np.maximum(0,np.sin(np.pi*h))**.53)*(1-h)**.77
        r=np.maximum(.0001,radius*swell*(1+.042*np.sin(2*t+phase+4*h)))
        path=cx+profile(h)+(.08*np.sin(t+phase+4*h)+.045*np.sin(2*t-phase-5*h))*h**1.35
        zline=depth+.13*h*np.cos(phase+t-2*h)
        px=path*np.cos(view)+zline*np.sin(view);pz=zline*np.cos(view)-path*np.sin(view)
        a=phase+.8*h+twist*h;wide=r;thin=.70*r
        co,si=np.cos(a),np.sin(a)
        aa=si**2/wide**2+co**2/thin**2
        bb=co*si*(1/wide**2-1/thin**2)
        cc=co**2/wide**2+si**2/thin**2
        dx=x-px;disc=aa+(bb**2-aa*cc)*dx**2
        z=pz+(-bb*dx+np.sqrt(np.maximum(disc,0)))/aa
        mask=(y>bottom)&(y<top)&(disc>0)
        dzdy,dzdx=np.gradient(z,ys,xs)
        n=unit(np.stack((-dzdx,-dzdy,np.ones_like(x)),axis=-1))
        diffuse=np.clip(n@light,0,1);rim=(1-n[:,:,2])**2
        shine=np.clip(n@half,0,1)**19
        if material=='core':
            base=mix(rgb('ffa14a'),rgb('fffbd1'),np.clip(.66*(1-h)+.43*diffuse,0,1))
            base=mix(base,np.ones(3),.53*shine)
        elif material=='gold':
            base=mix(mix(paint,rgb('f59453'),.60),rgb('fff1ab'),.20+.68*diffuse)
        else:
            # The far rim remains saturated. A moving reflected key gives the
            # rounded front a highlight distinctly separate from the silhouette.
            base=mix(paint*.52,mix(paint,np.ones(3),.40),.10+.83*diffuse)
            warm=np.clip(.10+.44*h+.11*n[:,:,0],0,.70)
            base=mix(base,rgb('ffb664'),warm*(.23+.70*diffuse))
            base=mix(base,np.ones(3),np.clip(.73*shine+.26*rim,0,.93))
        visible=mask&(z>depths);depths[visible]=z[visible];colors[visible]=base[visible]

    height=1.46+.08*np.sin(t)+.04*np.sin(2*t-.7)
    tongue(.035,height,.70,0,.0,.2,
           lambda h:-.13*np.sin(np.pi*h)+.31*h**2+.19*np.sin(t-.7)*h**2,'shell')
    tongue(.095,1.11+.11*np.sin(t+1),.35,-.20,.075,-.75,
           lambda h:-.17*np.sin(np.pi*h)-.16*h**2+.14*np.sin(t+2)*h**2,'shell')
    tongue(.085,1.07+.075*np.sin(2*t-.9),.38,.16,.16,1.2,
           lambda h:.19*np.sin(np.pi*h)-.065*h**2,'gold')
    tongue(.12,.79+.08*np.sin(t-.65),.31,-.015,.34,1.6,
           lambda h:-.038*np.sin(2*np.pi*h)+.16*h**2,'core')
    over(layer,colors,depths>-99)
    for side,offset in [(-1,.14),(1,.65)]:
        progress=(t/(2*np.pi)+offset)%1
        cx=side*(.24+.25*progress)+.065*np.sin(t+side)
        cy=.97+.68*progress;radius=.031+.016*np.sin(np.pi*progress)
        spark=np.exp(-1.25*(((x-cx)/radius)**2+((y-cy)/(1.52*radius))**2))
        over(layer,mix(paint,rgb('ffae69'),.58),spark*(np.sin(np.pi*progress)**1.5)*.95)
    # Rendered offscreen in premultiplied alpha; PNG stores straight alpha.
    layer[:,:,:3]/=np.maximum(layer[:,:,3:4],1e-8)
    return Image.fromarray(np.uint8(np.clip(layer,0,1)*255+.5))


def main():
    p=argparse.ArgumentParser();p.add_argument('--out',type=Path,required=True);p.add_argument('--study',action='store_true');a=p.parse_args()
    if a.study:
        canvas=Image.new('RGB',(6*120,3*116),'#faf8fb')
        for j,index in enumerate([1,2,6]):
            for i in range(6):
                im=render(2*np.pi*i/6,rgb(PALETTES[index])).resize((104,116),Image.Resampling.LANCZOS)
                canvas.paste(im,(i*120+8,j*116),im)
    else:
        rows=FRAMES//COLUMNS
        canvas=Image.new('RGBA',(COLUMNS*WIDTH,len(PALETTES)*rows*HEIGHT))
        for j,hexcolor in enumerate(PALETTES):
            for i in range(FRAMES):
                frame=render(2*np.pi*i/FRAMES,rgb(hexcolor))
                canvas.paste(frame,(i%COLUMNS*WIDTH,(j*rows+i//COLUMNS)*HEIGHT))
    a.out.parent.mkdir(parents=True,exist_ok=True);canvas.save(a.out,optimize=True)


if __name__=='__main__':main()
