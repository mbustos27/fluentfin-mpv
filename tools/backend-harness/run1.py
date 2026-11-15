import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import probe
for n in sys.argv[1:]:
    getattr(probe, "s_" + n)()
