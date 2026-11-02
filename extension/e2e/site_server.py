import http.server, functools, os, threading, json

SITE = os.path.join(os.environ['TEMP'], 'swiftget-e2e', 'site')
LOG = os.path.join(os.environ['TEMP'], 'swiftget-e2e', 'requests.log')

class H(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw): super().__init__(*a, directory=SITE, **kw)
    def log_message(self, *a): pass
    def do_GET(self):
        with open(LOG, 'a', encoding='utf-8') as f:
            f.write(self.path + "\n")
        super().do_GET()

httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 49777), H)
print("server uji jalan di 49777")
httpd.serve_forever()
