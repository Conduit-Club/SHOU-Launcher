"""Import string values only from the v1.5.0 language dictionaries; never read WPF views/styles."""
from pathlib import Path
import hashlib
import html
import json
import re
import subprocess
import xml.etree.ElementTree as ET

repo = Path(__file__).resolve().parents[2]
dest = repo / 'src/SHOU.Launcher.Desktop/Localization/Resources'
dest.mkdir(parents=True, exist_ok=True)
languages = ['zh-CN', 'zh-TW', 'en-US', 'en-GB', 'ja-JP', 'fr-FR', 'es-ES']
legacy = {}
for language in languages:
    content = subprocess.check_output(['git', 'show', f'v1.5.0:PCL.Core/App/Localization/Languages/{language}.xaml'], cwd=repo)
    legacy[language] = {e.attrib['{http://schemas.microsoft.com/winfx/2006/xaml}Key']: e.text or '' for e in ET.fromstring(content) if e.tag.endswith('String')}
overrides = json.loads((dest.parent / 'supplement-en.json').read_text(encoding='utf-8'))
phrases = {}
for key, phrase in legacy['zh-CN'].items():
    if phrase.strip(): phrases.setdefault(phrase, key)
if (dest / 'zh-CN.json').is_file():
    for phrase in json.loads((dest / 'zh-CN.json').read_text(encoding='utf-8')).values(): phrases.setdefault(phrase, None)
literal_pattern = re.compile(r'\b(Text|Content|Header|PlaceholderText|ToolTip.Tip|AutomationProperties.Name)="([^"]*)"')
view_literals = set()
for view in (repo / 'src/SHOU.Launcher.Desktop/Views').glob('*.axaml'):
    source = view.read_text(encoding='utf-8-sig')
    def replace(match):
        attribute, raw = match.groups()
        phrase = html.unescape(raw)
        if raw.startswith('{') or not re.search(r'[\u4e00-\u9fff]', phrase): return match.group(0)
        phrases.setdefault(phrase, None); view_literals.add(phrase)
        token = 't' + hashlib.sha256(phrase.encode()).hexdigest()[:16]
        return f'{attribute}="{{loc:Loc {token}}}"'
    rewritten = literal_pattern.sub(replace, source)
    if rewritten != source:
        if 'xmlns:loc=' not in rewritten: rewritten = rewritten.replace('xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"', 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:loc="using:SHOU.Launcher.Desktop.Localization"', 1)
        view.write_text(rewritten, encoding='utf-8')
phrases.update({phrase: phrases.get(phrase) for phrase in overrides})
for language in languages:
    catalog = {}
    for phrase, old_key in phrases.items():
        value = phrase if language == 'zh-CN' else legacy[language].get(old_key) if old_key else None
        if language in ('en-US', 'en-GB') and phrase in overrides: value = overrides[phrase]
        if value:
            token = 't' + hashlib.sha256(phrase.encode()).hexdigest()[:16]
            assert token not in catalog or catalog[token] == value
            catalog[token] = value
    (dest / f'{language}.json').write_text(json.dumps(catalog, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps({'source': 'v1.5.0 string dictionaries only', 'phrases': len(phrases), 'viewLiteralsImported': len(view_literals), 'englishSupplement': len(overrides)}, ensure_ascii=False))
