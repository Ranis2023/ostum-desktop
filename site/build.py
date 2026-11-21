"""Render the public guides into the GitHub Pages publishing directory."""

from html import escape
from pathlib import Path
from string import Template
import json
import xml.etree.ElementTree as ET

SOURCE = Path(__file__).resolve().parent
DEST = SOURCE.parent / "docs"
SITE = "https://ranis2023.github.io/ostum-desktop/"
REPO = "https://github.com/Ranis2023/ostum-desktop"
INSTALLER = REPO + "/releases/latest/download/Ostum-Desktop-Setup.exe"
LANGUAGES = {
    "ru": dict(home="Главная", guides="Руководства", download="Скачать для Windows", source="Исходный код", menu="Главное меню", links="Ссылки проекта", locale="ru_RU",
               community="Обсуждения", other="English", more="Другие руководства", skip="К содержанию"),
    "en": dict(home="Home", guides="Guides", download="Download for Windows", source="Source code", menu="Main menu", links="Project links", locale="en_US",
               community="Discussions", other="Русский", more="More guides", skip="Skip to content"),
}


def route(lang, key):
    prefix = "" if lang == "ru" else "en/"
    return prefix if key == "home" else prefix + key + "/"


def build():
    pages = json.loads((SOURCE / "pages.json").read_text("utf-8"))
    template = Template((SOURCE / "layout.html").read_text("utf-8"))
    sitemap = ET.Element("urlset", xmlns="http://www.sitemaps.org/schemas/sitemap/0.9")
    for lang, words in LANGUAGES.items():
        other = "en" if lang == "ru" else "ru"
        for key, meta in pages[lang].items():
            path = route(lang, key)
            destination = DEST / path
            destination.mkdir(parents=True, exist_ok=True)
            base = "../" * path.count("/") if path else "./"
            home = base + route(lang, "home")
            canonical = SITE + path
            title, description = meta["title"], meta["description"]
            entry = dict(words, base=base, home_url=home, lang=lang, other_lang=other, repo=REPO, installer=INSTALLER,
                         ru_url=SITE + route("ru", key), en_url=SITE + route("en", key), other_url=base + route(other, key),
                         url=canonical, site=SITE, head_title=escape(meta["browser"]), title=escape(title),
                         description=escape(description), kind="website" if key=="home" else "article",
                         other_locale=LANGUAGES[other]["locale"],
                         card_file=meta.get("cover", "social-card.png"),
                         card_alt=escape(meta.get("cover_alt", "OSTUM Desktop 5.7.0 — " + ("окно локального протокола совещания" if lang=="ru" else "local meeting minutes with timestamps"))))
            content = Template((SOURCE / "content" / lang / (key + ".html")).read_text("utf-8")).substitute(entry)
            cards = []
            for guide, info in pages[lang].items():
                if guide in ("home", key):
                    continue
                cards.append('<a class="guide-card" href="{}"><span class="topic">{}</span><h3>{}</h3><p>{}</p><span class="arrow" aria-hidden="true">↗</span></a>'.format(
                    base+route(lang,guide), escape(info["topic"]), escape(info["title"]), escape(info["blurb"])))
            guide_grid = '<div class="guide-grid">' + "".join(cards) + '</div>'
            if key == "home":
                body = content + '<section class="wrap guide-section" id="guides"><h2>'+words["guides"]+'</h2>'+guide_grid+'</section>'
                schema = {"@type":"SoftwareApplication","name":"OSTUM Desktop","applicationCategory":"ProductivityApplication",
                          "operatingSystem":"Windows 10/11 x64","downloadUrl":INSTALLER,"url":canonical,"softwareVersion":meta["version"]}
            else:
                body = '<main class="wrap reading" id="main"><nav class="crumb"><a href="'+home+'">'+words["home"]+'</a><span aria-hidden="true"> / </span>'+escape(meta["topic"])+'</nav><article><h1>'+escape(title)+'</h1><p class="dek">'+escape(description)+'</p>'+content+'</article></main><section class="wrap guide-section"><h2>'+words["more"]+'</h2>'+guide_grid+'</section>'
                schema = {"@type":"TechArticle","headline":title,"description":description,"url":canonical,"inLanguage":lang,
                          "author":{"@type":"Organization","name":"OSTUM Desktop","url":REPO}}
            schema["@context"] = "https://schema.org"
            entry.update(body=body, structured=json.dumps(schema, ensure_ascii=False).replace("<","\\u003c"))
            (destination/"index.html").write_text(template.substitute(entry), encoding="utf-8", newline="\n")
            ET.SubElement(ET.SubElement(sitemap, "url"), "loc").text = canonical
    ET.indent(sitemap, space="  ")
    ET.ElementTree(sitemap).write(DEST/"sitemap.xml", encoding="utf-8", xml_declaration=True)
    (DEST/".nojekyll").touch()


if __name__ == "__main__":
    build()
