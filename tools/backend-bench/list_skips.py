"""List skipped tests and their reasons (stdlib). Off-screen + sandboxed via run_suite env.
    python list_skips.py <shim root>"""
import collections, os, sys, unittest
root = sys.argv[1]
sys.argv = [sys.argv[0]]
sys.path.insert(0, root)
os.chdir(root)
suite = unittest.TestLoader().discover("tests")
class R(unittest.TextTestResult):
    pass
res = unittest.TextTestRunner(stream=open(os.devnull, "w"), resultclass=R).run(suite)
c = collections.Counter(r for _t, r in res.skipped)
print("skipped", len(res.skipped), "failures", len(res.failures), "errors", len(res.errors), "ran", res.testsRun)
for reason, n in c.most_common():
    print("%3d  %s" % (n, reason.splitlines()[0][:150]))
sys.stdout.flush()
os._exit(0)
