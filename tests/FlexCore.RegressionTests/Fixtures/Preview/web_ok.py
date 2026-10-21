"""Tiny HTTP server for the FlexCore web-preview checks.

Listens on FLEXCORE_PREVIEW_PORT. The first argument, when present, must equal that port.
"""
import os
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

port = int(os.environ["FLEXCORE_PREVIEW_PORT"])
if len(sys.argv) > 1 and sys.argv[1] != str(port):
    raise SystemExit("port token was " + sys.argv[1])


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path == "/html":
            body = (
                '<link href="/app.css">'
                '<a href="//cdn.example/lib">c</a>'
                '<img src="/pic.png">'
                '<form action="/save"></form>'
                '<div style="background:url(/bg.png)"></div>'
            ).encode()
            self._send(200, "text/html; charset=utf-8", body, extra=[("X-Frame-Options", "DENY"), ("Content-Security-Policy", "frame-ancestors 'none'")])
            return
        if path == "/echo":
            cookie = self.headers.get("Cookie") or ""
            self._send(200, "text/plain", ("cookie:" + cookie).encode())
            return
        if path == "/redirect":
            self.send_response(302)
            self.send_header("Location", "/next")
            self.end_headers()
            return
        urls = os.environ.get("ASPNETCORE_URLS", "")
        base = os.environ.get("ASPNETCORE_PATHBASE", "")
        body = f"preview-web-ok {self.path} urls={urls} base={base}".encode()
        self._send(200, "text/plain", body, extra=[("X-Frame-Options", "DENY")])

    def _send(self, status, content_type, body, extra=()):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        for name, value in extra:
            self.send_header(name, value)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        return


ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
