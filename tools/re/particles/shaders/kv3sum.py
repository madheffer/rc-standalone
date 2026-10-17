import sys, re, json

def tokenize(s):
    i = 0; n = len(s)
    while i < n:
        c = s[i]
        if c.isspace(): i += 1; continue
        if s.startswith('<!--', i):
            i = s.index('-->', i) + 3; continue
        if c in '{}[]=,': yield c; i += 1; continue
        if c == '"':
            j = i + 1; out = []
            while s[j] != '"':
                if s[j] == '\\': out.append(s[j+1]); j += 2
                else: out.append(s[j]); j += 1
            yield ('s', ''.join(out)); i = j + 1; continue
        if s.startswith('#[', i):
            j = s.index(']', i); yield ('b', s[i+2:j].strip()); i = j + 1; continue
        m = re.compile(r'[^\s{}\[\]=,]+').match(s, i)
        yield ('a', m.group(0)); i = m.end()

def parse(tokens):
    t = next(tokens)
    return value(t, tokens)

def value(t, tokens):
    if t == '{':
        d = {}
        while True:
            k = next(tokens)
            if k == '}': return d
            if k == ',': continue
            eq = next(tokens); assert eq == '=', (k, eq, list(d.items())[-2:])
            d[k[1]] = value(next(tokens), tokens)
    if t == '[':
        a = []
        while True:
            k = next(tokens)
            if k == ']': return a
            if k == ',': continue
            a.append(value(k, tokens))
    if t[0] == 's' or t[0] == 'b': return t[1]
    v = t[1]
    if v in ('true', 'false'): return v == 'true'
    try: return int(v)
    except: pass
    try: return float(v)
    except: return v

def load(path):
    s = open(path, encoding='utf-8', errors='replace').read()
    i = s.index('--- Data for block "DATA" ---')
    s = s[i:]
    s = s[s.index('{', s.index('-->')):]
    # cut at next block marker
    j = s.find('\n--- Data for block')
    if j > 0: s = s[:j]
    return parse(tokenize(s))

if __name__ == '__main__':
    d = load(sys.argv[1])
    root = d.get('m_programData', d)
    for key in ('m_staticComboArray', 'm_dynamicComboArray'):
        print('==', key)
        for c in root.get(key, []):
            print(f"  {c['m_szName']:40s} {c.get('m_comboType','')} [{c['m_nMin']}..{c['m_nMax']}] x{c['m_nComboIndexValue']} {c.get('m_stringArray') or ''}")
    for key in ('m_staticComboRuleArray', 'm_dynamicComboRuleArray'):
        if key in root: print('==', key, len(root[key]))
    if len(sys.argv) > 2:
        json.dump(d, open(sys.argv[2], 'w'), indent=1)
    print('keys', list(root.keys()))
