"""Minimal KV3 text parser (VRF / tier0 output)."""
import re
BS = chr(92)
TOK = re.compile(r'''\s*(?:(//[^\n]*)|(/\*.*?\*/)|(<!--.*?-->)|(""".*?""")|("(?:[^"\\]|\\.)*")|([\[\]{}=,])|(\#\[)|([A-Za-z_][\w.]*:"(?:[^"\\]|\\.)*")|([^\s\[\]{}=,"]+))''', re.S)


class Typed(str):
    pass


def tokens(t):
    pos = 0
    n = len(t)
    while pos < n:
        m = TOK.match(t, pos)
        if not m:
            if t[pos:].strip() == '':
                return
            raise ValueError(f'bad at {pos}: {t[pos:pos+40]!r}')
        pos = m.end()
        if m.group(1) or m.group(2) or m.group(3):
            continue
        g = m.group(0).strip()
        if g:
            yield g


def parse(text):
    it = list(tokens(text))
    i = 0

    def val():
        nonlocal i
        tk = it[i]
        i += 1
        if tk == '{':
            o = {}
            while it[i] != '}':
                k = it[i]
                i += 1
                if k.startswith('"'):
                    k = k[1:-1]
                assert it[i] == '=', (k, it[i:i+3])
                i += 1
                o[k] = val()
            i += 1
            return o
        if tk == '[':
            a = []
            while it[i] != ']':
                a.append(val())
                if it[i] == ',':
                    i += 1
            i += 1
            return a
        if tk == '#[':
            b = []
            while it[i] != ']':
                b.append(it[i])
                i += 1
            i += 1
            return {'#blob': ' '.join(b)}
        if tk.startswith('"""'):
            return tk[3:-3]
        if tk.startswith('"'):
            s = tk[1:-1]
            return s.replace(BS + '"', '"').replace(BS + BS, BS) if BS in s else s
        if re.match(r'[A-Za-z_][\w.]*:"', tk):
            return Typed(tk)
        if tk == 'true':
            return True
        if tk == 'false':
            return False
        if tk == 'null':
            return None
        try:
            return int(tk)
        except ValueError:
            try:
                return float(tk)
            except ValueError:
                return Typed(tk)
    return val()
