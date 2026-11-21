"""Create synthetic invoices, attachments and training pairs for workday QA on Windows."""
import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas


def create(folder):
    folder.mkdir(parents=True, exist_ok=True)
    font = Path("C:/Windows/Fonts/arial.ttf")
    pdfmetrics.registerFont(TTFont("QA", str(font)))
    pdf = canvas.Canvas(str(folder / "invoice.pdf"), pagesize=(520, 680))
    for lines in [
        ["Поставка материалов", "Учебный заказ ВР-1024.", "Предварительная стоимость 9800 рублей."],
        ["Окончательный расчёт", "Код приёмки ТК-4172.", "Согласованная сумма 7340 рублей.", "Ответственный за приёмку Марина Тихая."],
    ]:
        pdf.setFont("QA", 13)
        for i, line in enumerate(lines):
            pdf.drawString(40, 588 - 31 * i, line)
        pdf.showPage()
    pdf.save()
    for i, (number, amount) in enumerate([("3174", "12680"), ("5408", "20940"), ("8261", "17250")], 1):
        paper = Image.new("RGB", (1080, 630), "white")
        draw = ImageDraw.Draw(paper)
        for y, label in enumerate(["Накладная " + number, "Сумма " + amount + " рублей", "Проверил отдел приёмки"]):
            draw.text((70, 95 + y * 100), label, font=ImageFont.truetype(str(font), 48), fill="#161616")
        if i == 3:
            paper = paper.rotate(1.2, Image.Resampling.BICUBIC, fillcolor="white")
        scan = folder / ("scan-" + str(i) + ".png")
        paper.save(scan)
        raster = canvas.Canvas(str(scan.with_suffix(".pdf")), pagesize=(540, 315))
        raster.drawImage(str(scan), 0, 0, 540, 315)
        raster.save()
    Image.new("RGB", (128, 128), "#1945df").save(folder / "blue.png")
    (folder / "old.txt").write_text("Код КД-428. Срок поставки 12 дней. Начальная стоимость 6100 рублей.\n", encoding="utf-8")
    (folder / "new.txt").write_text("Код КД-428. Срок поставки 9 дней. Согласованная стоимость 6500 рублей.\n", encoding="utf-8")
    (folder / "offers.csv").write_text("Поставщик;Код;Сумма\nБерёза;СК-0098;9460\nЛипа;СК-0089;5820\n", encoding="utf-8-sig")
    (folder / "empty.txt").write_text("", encoding="utf-8")
    (folder / "large.txt").write_text(("Принята обычная поставка на склад.\n" * 21000) + "Код в самом конце ЛБ-714.\n", encoding="utf-8")
    pairs = [
        ("Как маркируется синий лот?", "СИНИЙ", "lot"),
        ("Куда кладут запасной ключ?", "СЕЙФ", "key"),
        ("Кто сверяет тираж?", "ПЕЧАТНИК", "print"),
        ("Назови цвет бирки северной зоны.", "БЕЛЫЙ", "tag"),
        ("Как называется ночная смена?", "ЛУНА", "shift"),
        ("Какая папка нужна для бланков?", "ФОРМЫ", "folder"),
    ]
    (folder / "pairs.json").write_text(json.dumps([dict(prompt=p, answer=a, group=g) for p, a, g in pairs], ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    create(parser.parse_args().directory)
