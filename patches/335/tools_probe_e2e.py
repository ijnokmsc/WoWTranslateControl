# -*- coding: utf-8 -*-
# 假 OpenAI 兼容服务器：POST /v1/chat/completions → 回显 choices[0].message.content="你好世界"
import socket, threading, sys

def serve():
    srv = socket.socket()
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(('127.0.0.1', 18081))
    srv.listen(4)
    while True:
        c, _ = srv.accept()
        try:
            data = b''
            while b'\r\n\r\n' not in data:
                data += c.recv(65536)
            head, _, rest = data.partition(b'\r\n\r\n')
            clen = 0
            for line in head.decode('latin1').split('\r\n'):
                if line.lower().startswith('content-length:'):
                    clen = int(line.split(':')[1].strip())
            while len(rest) < clen:
                rest += c.recv(65536)
            data = rest.decode('utf-8', errors='replace')
            body = ('{"id":"t","object":"chat.completion","model":"fake",'
                    '"choices":[{"index":0,"message":{"role":"assistant","content":"你好世界"},'
                    '"finish_reason":"stop"}]}')
            if 'messages' not in data:
                body = '{"error":"bad request"}'
            resp = ('HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n'
                    'Content-Length: ' + str(len(body.encode('utf-8'))) + '\r\nConnection: close\r\n\r\n' + body)
            c.sendall(resp.encode('utf-8'))
        except Exception as e:
            print('conn err', e)
        finally:
            c.close()

threading.Thread(target=serve, daemon=True).start()
print('fake openai on 18081 ready')
import subprocess, os, time
time.sleep(0.3)
r = subprocess.run([r'D:\GitHub\WoWTranslateControl\patches\335\build\selftest_host.exe',
                    'http://127.0.0.1:18081/v1/chat/completions'],
                   capture_output=True, timeout=60)
print(r.stdout.decode('gbk', errors='replace'))
print('RC=', r.returncode)
