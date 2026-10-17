import sys, pickle, os
sys.path.insert(0,'.')
from pe import PE
def load(path):
    cache = path + '.refs.pkl'
    p = PE(path)
    if os.path.exists(cache):
        p.__dict__['riprefs'] = pickle.load(open(cache,'rb'))
    else:
        r = p.riprefs
        pickle.dump(r, open(cache,'wb'))
    return p
if __name__ == '__main__':
    p = load(sys.argv[1])
    for a in sys.argv[2:]:
        print(a, [hex(x) for x in p.riprefs.get(int(a,16), [])])
