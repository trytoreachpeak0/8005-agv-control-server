import sys, xml.etree.ElementTree as ET, collections, datetime as dt, statistics as st
ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def ts(s):
    # 2026-10-10T09:31:00.1234567+08:00
    if '.' in s:
        a,b=s.split('.',1); frac=''; i=0
        while i<len(b) and b[i].isdigit(): frac+=b[i]; i+=1
        s=a+'.'+frac[:6]+b[i:]
    return dt.datetime.fromisoformat(s)
def dur(s):
    h,m,x=s.split(':'); return int(h)*3600+int(m)*60+float(x)
def load(p):
    r=ET.parse(p).getroot()
    defs={}
    for u in r.iterfind('.//t:UnitTest',ns):
        tm=u.find('t:TestMethod',ns); defs[u.get('id')]=(tm.get('className'),tm.get('name'))
    res=[]
    for x in r.iterfind('.//t:UnitTestResult',ns):
        c,n=defs[x.get('testId')]
        res.append(dict(cls=c.split('.')[-1],full=c,name=x.get('testName'),out=x.get('outcome'),
            d=dur(x.get('duration','0:0:0')),s=ts(x.get('startTime')),e=ts(x.get('endTime'))))
    return res
