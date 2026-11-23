import json, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs

def sse(objs, done=False):
    out = "".join("data: " + json.dumps(o) + "\n\n" for o in objs)
    if done: out += "data: [DONE]\n\n"
    return out

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, code, body, ctype="text/event-stream"):
        b = body.encode()
        self.send_response(code); self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        if self.path.startswith("/local/v1/models"):
            self.send(200, json.dumps({"object":"list","data":[{"id":"qwen2.5vl:7b"},{"id":"nomic-embed-text"},{"id":"gemma4:e4b"}]}), "application/json")
        else: self.send(404, "{}", "application/json")
    def do_POST(self):
        n = int(self.headers.get("Content-Length", 0)); body = json.loads(self.rfile.read(n))
        u = urlparse(self.path); q = parse_qs(u.query); prov = u.path.split("/")[1]
        h = self.headers
        errs = []
        if prov == "gemini":
            if "generativelanguage.googleapis.com/v1beta/models/m1:streamGenerateContent?alt=sse" not in q["orig"][0]: errs.append("url")
            if h.get("x-goog-api-key") != "k": errs.append("key")
            parts = body["contents"][0]["parts"]
            if parts[0]["inlineData"]["data"] != "QUJD" or parts[1]["text"] != "olá" or body["systemInstruction"]["parts"][0]["text"] != "sys": errs.append("body")
            if errs: return self.send(400, json.dumps({"error":{"message":",".join(errs)}}), "application/json")
            return self.send(200, sse([{"candidates":[{"content":{"parts":[{"text":"pensando","thought":True},{"text":"Olá, "}]}}]},{"candidates":[{"content":{"parts":[{"text":"mundo!"}]},"finishReason":"STOP"}]}]))
        if prov == "openai":
            if q["orig"][0] != "https://api.openai.com/v1/responses": errs.append("url")
            if h.get("Authorization") != "Bearer k": errs.append("auth")
            c = body["input"][0]["content"]
            if body["model"]!="m1" or not body["stream"] or c[0]["type"]!="input_image" or not c[0]["image_url"].startswith("data:image/jpeg;base64,QUJD") or c[1]["text"]!="olá" or body["instructions"]!="sys": errs.append("body")
            if errs: return self.send(400, json.dumps({"error":{"message":",".join(errs)}}), "application/json")
            return self.send(200, "event: x\n" + sse([{"type":"response.created"},{"type":"response.output_text.delta","delta":"Olá, "},{"type":"response.output_text.delta","delta":"mundo!"},{"type":"response.completed"}]))
        if prov == "claude":
            if q["orig"][0] != "https://api.anthropic.com/v1/messages": errs.append("url")
            if h.get("x-api-key") != "k" or h.get("anthropic-version") != "2023-06-01": errs.append("hdr")
            c = body["messages"][0]["content"]
            if c[0]["source"]["data"]!="QUJD" or c[1]["text"]!="olá" or body["system"]!="sys" or not body["max_tokens"]: errs.append("body")
            if errs: return self.send(400, json.dumps({"error":{"message":",".join(errs)}}), "application/json")
            return self.send(200, sse([{"type":"message_start"},{"type":"content_block_delta","delta":{"type":"text_delta","text":"Olá, "}},{"type":"content_block_delta","delta":{"type":"text_delta","text":"mundo!"}},{"type":"message_delta","delta":{"stop_reason":"end_turn"}}]))
        if prov == "local":
            if body["model"] == "missing": return self.send(404, json.dumps({"error":{"message":"model 'missing' not found"}}), "application/json")
            if body["model"] == "badkey": return self.send(401, json.dumps({"error":"unauthorized"}), "application/json")
            m = body["messages"]
            if m[0]["role"]!="system": errs.append("sys")
            if "images" in sys.argv: pass
            if errs: return self.send(400, json.dumps({"error":{"message":",".join(errs)}}), "application/json")
            return self.send(200, sse([{"choices":[{"delta":{"role":"assistant","content":"<think>hmm"}}]},{"choices":[{"delta":{"content":"</think>Olá, "}}]},{"choices":[{"delta":{"content":"mundo!"},"finish_reason":None}]},{"choices":[{"delta":{},"finish_reason":"stop"}]}], done=True))
        self.send(404, "{}", "application/json")

ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), H).serve_forever()
