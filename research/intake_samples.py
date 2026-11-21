"""Create an independent, repeatable scan corpus without using anyone's invoices."""
import json
import random
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter
from reportlab.pdfgen.canvas import Canvas
from reportlab.lib.utils import ImageReader

folder=Path(sys.argv[1]);folder.mkdir(parents=True,exist_ok=True)
rng=random.Random(4279)
records=[]
for batch in range(2):
    pdf=Canvas(str(folder/("batch%d.pdf"%batch)),pagesize=(570,360))
    for page in range(1,26):
        index=batch*25+page
        number=str(7312+17*index);amount=str(12041+73*index)
        im=Image.new("RGB",(1140,720),tuple([rng.randrange(239,256)]*3))
        draw=ImageDraw.Draw(im)
        face=ImageFont.truetype(str(Path("C:/Windows/Fonts")/rng.choice(["arial.ttf","segoeui.ttf","times.ttf"])),rng.randint(27,48))
        x=rng.randint(48,100)
        for line,s in enumerate([rng.choice(["Накладная ","Счёт № "])+number,"от 06.11.2026",rng.choice(["Итого к оплате ","Сумма "])+amount+" рублей","Отдел приёмки"]):
            draw.text((x,90+line*85),s,font=face,fill="#282828")
        if index%3==0:draw.ellipse((806,433,987,590),outline="#7487aa",width=3)
        if index%4==0:im=im.filter(ImageFilter.GaussianBlur(.65))
        im=im.rotate(rng.uniform(-1.8,1.8),Image.Resampling.BICUBIC,fillcolor="white")
        pdf.drawImage(ImageReader(im),0,0,570,360);pdf.showPage()
        records.append(dict(batch=batch,page=page,number=number,amount=amount))
    pdf.save()
(folder/"answers.json").write_text(json.dumps(records),encoding="utf-8")
