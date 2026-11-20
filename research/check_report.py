"""Validate the offline document, results archive and local links."""
import argparse
from html.parser import HTMLParser
import json
from pathlib import Path
from urllib.parse import unquote,urlparse
from zipfile import ZipFile


class Page(HTMLParser):
    def __init__(self):
        super().__init__()
        self.sections=[];self.links=[];self.remote_assets=[];self.tables=0
    def handle_starttag(self,tag,attrs):
        a=dict(attrs)
        if tag=='section':self.sections.append(a.get('id'))
        if tag=='table':self.tables+=1
        if tag=='a' and a.get('href'):self.links.append(a['href'])
        if tag in ('img','script','link') and any(urlparse(a.get(k,'')).netloc for k in ('src','href')):self.remote_assets.append(tag)

def main():
    p=argparse.ArgumentParser();p.add_argument('directory',type=Path);a=p.parse_args();directory=a.directory.resolve()
    html=directory/'Отчёт.html';page=Page();page.feed(html.read_text(encoding='utf8'))
    assert page.sections==list('ABCDEF') and page.tables>=7 and not page.remote_assets
    for link in page.links:
        url=urlparse(link)
        if url.scheme:continue
        if url.path:assert (directory/unquote(url.path)).is_file(),link
        if url.fragment:assert url.fragment in page.sections,link
    results=json.loads((directory/'Результаты.json').read_text(encoding='utf8'))
    assert results['version']=='3.3.0.0' and results['installation']['matching_binary'] and all(results['controls'].values())
    assert results['standalone']['version']==results['version'] and results['sourcecheck_window']['matches_native']==results['sourcecheck_window']['total']
    with ZipFile(directory/'Материалы.zip') as z:
        assert z.testzip() is None
        assert 'research/reproduce.ps1' in z.namelist() and 'src/Inference.cs' in z.namelist()
        for name in z.namelist():
            assert not any(s in name for s in ('audit-independent-formats','extra-heldout-pool','work-formats.gguf','/external.json','/learning/')),name
    print('Validated sections, tables, links, archive, installed binaries, compact-model bundle and desktop verdicts.')

if __name__=='__main__':main()
