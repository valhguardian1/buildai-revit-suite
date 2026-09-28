import os, re, io, base64, shutil, zipfile
from gen import *
SRC='/mnt/user-data/uploads/revit-plugin/BuildAI-7.0.4/src/'
OUT='out/BuildAI-ribbon-icons'
shutil.rmtree('out', ignore_errors=True)
PLUG={'Plugin2.VolumeEstimator':{'ribbonicon':'results','recalculate':'recalculate','open':'open','connect':'connect'},
      'Plugin3.LinkChangeMonitor':{'ribbonicon':'checklinks','checklinks':'checklinks','changes':'changes'},
      'Plugin4.LinkComparatorAI':{'ribbonicon':'compare','compare':'compare','rooms':'rooms','results':'results','settings':'settings'},
      'Plugin5.ClashFormaIntegration':{'ribbonicon':'clash','clash':'clash','results':'results','createview':'createview','publish':'publish','settings':'settings'}}
NEW=['support','syncback']
def pngbytes(name,size,col):
    im=png(name,size,col); b=io.BytesIO()
    im.save(b,'PNG',dpi=(192,192) if size==64 else (96,96), optimize=True); return b.getvalue()
for v,col in VARIANTS.items():
    base=f'{OUT}/{v}'
    for d in ('png16','png32','png64-hidpi','svg'): os.makedirs(f'{base}/{d}',exist_ok=True)
    for n in ORDER:
        for sz,d in ((16,'png16'),(32,'png32'),(64,'png64-hidpi')):
            open(f'{base}/{d}/{n}{sz}.png','wb').write(pngbytes(n,sz,col))
        open(f'{base}/svg/{n}16.svg','w').write(svg(n,16,col)); open(f'{base}/svg/{n}32.svg','w').write(svg(n,32,col))
    # drop-in per plugin
    for p,m in PLUG.items():
        idir=f'{base}/drop-in/{p}/Resources/icons'; os.makedirs(idir,exist_ok=True); os.makedirs(f'{base}/drop-in/{p}/Revit',exist_ok=True)
        cs=open(f'{SRC}{p}/Revit/EmbeddedIconData.cs',encoding='utf-8-sig').read()
        keys=dict(m); keys.update({k:k for k in NEW})
        for key,icon in keys.items():
            for sz in (16,32):
                data=pngbytes(icon,sz,col)
                open(f'{idir}/{key}{sz}.png','wb').write(data)
                b64=base64.b64encode(data).decode()
                pat=re.compile(r'(\{ "'+key+str(sz)+r'", ")[A-Za-z0-9+/=]+(" \},)')
                if pat.search(cs): cs=pat.sub(lambda mm: mm.group(1)+b64+mm.group(2),cs)
                else: cs=cs.replace('        };\n',f'            {{ "{key}{sz}", "{b64}" }},\n        }};\n',1)
        open(f'{base}/drop-in/{p}/Revit/EmbeddedIconData.cs','w',encoding='utf-8-sig').write(cs)
print('ok')
