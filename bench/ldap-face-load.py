#!/usr/bin/env python3
"""Load generator for dmart's LDAP directory face (Ldap/).

Standard library only: it carries its own few dozen lines of BER, so it runs
anywhere without python-ldap or ldap3. Each worker holds one connection, binds
once as a service account, then repeats one workload as fast as the server
answers, the way Postfix and Dovecot reuse a connection.

Workloads, named for the client whose query they reproduce:

  uid      Dex / Gitea:      (&(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail))(uid=uNNNNNNN))
  mail     Postfix mailbox:  (&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mail=uNNNNNNN@bench.test))
  alias    Postfix alias:    (&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mailAlias=aliasN@bench.test))
  bind     Dovecot login:    a simple bind as uid=uNNNNNNN (pays for an Argon2 verify)

Users are expected to be named u0000001..uNNNNNNN, as bench/REPORT-ldap-face.md
seeds them.

  bench/ldap-face-load.py --port 13389 --base dc=bench --users 1000000 \\
      --workload uid --conns 8 --seconds 10
"""
import argparse
import random
import socket
import statistics
import threading
import time
from collections import Counter


# ---- BER, only what LDAP requests need ----

def _len(n):
    if n < 0x80:
        return bytes([n])
    b = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(b)]) + b


def tlv(tag, content):
    return bytes([tag]) + _len(len(content)) + content


def octets(s, tag=0x04):
    return tlv(tag, s.encode())


def integer(v, tag=0x02):
    return tlv(tag, v.to_bytes(max(1, (v.bit_length() + 8) // 8), "big", signed=True))


def seq(*parts, tag=0x30):
    return tlv(tag, b"".join(parts))


def eq(attr, value):
    return seq(octets(attr), octets(value), tag=0xA3)


def and_(*filters):
    return seq(*filters, tag=0xA0)


def bind_request(mid, dn, password):
    return seq(integer(mid), seq(integer(3), octets(dn), octets(password, 0x80), tag=0x60))


def search_request(mid, base, flt, attrs):
    return seq(integer(mid), seq(
        octets(base), integer(2, 0x0A), integer(0, 0x0A), integer(0), integer(0),
        tlv(0x01, b"\x00"), flt, seq(*[octets(a) for a in attrs]), tag=0x63))


class Conn:
    def __init__(self, host, port):
        self.sock = socket.create_connection((host, port))
        self.sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.mid = 0

    def _read(self, n):
        buf = b""
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("server closed the connection")
            buf += chunk
        return buf

    def _message(self):
        head = self._read(2)
        n = head[1]
        if n & 0x80:
            n = int.from_bytes(self._read(n & 0x7F), "big")
        body = self._read(n)
        # messageID INTEGER, then the protocolOp tag
        id_len = body[1]
        op_tag = body[2 + id_len]
        op = body[2 + id_len:]
        return op_tag, op

    @staticmethod
    def _code(op):
        # [APPLICATION n] { resultCode ENUMERATED, ... }
        i = 1
        n = op[i]
        i += 1 + (n & 0x7F if n & 0x80 else 0)
        return op[i + 2]

    def send(self, data):
        self.sock.sendall(data)

    def bind(self, dn, password):
        self.mid += 1
        self.send(bind_request(self.mid, dn, password))
        _, op = self._message()
        return self._code(op)

    def search(self, base, flt, attrs=("mail",)):
        self.mid += 1
        self.send(search_request(self.mid, base, flt, list(attrs)))
        entries = 0
        while True:
            tag, op = self._message()
            if tag == 0x64:
                entries += 1
            elif tag == 0x65:
                return self._code(op), entries


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=13389)
    ap.add_argument("--base", default="dc=bench")
    ap.add_argument("--users", type=int, required=True)
    ap.add_argument("--workload", choices=["uid", "mail", "alias", "bind"], required=True)
    ap.add_argument("--conns", type=int, default=1)
    ap.add_argument("--seconds", type=float, default=10)
    ap.add_argument("--service", default="mail")
    ap.add_argument("--password", default="Bench12345")
    args = ap.parse_args()

    people = f"ou=people,{args.base}"
    service_dn = f"cn={args.service},ou=services,{args.base}"
    gate = (eq("objectClass", "freexUser"), eq("isActive", "TRUE"), eq("authorizedService", "mail"))

    latencies, codes, lock = [], Counter(), threading.Lock()
    deadline = time.monotonic() + args.seconds

    def worker(seed):
        rnd = random.Random(seed)
        c = Conn(args.host, args.port)
        if args.workload != "bind":
            code = c.bind(service_dn, args.password)
            if code != 0:
                raise SystemExit(f"service bind failed with {code}")
        mine, my_codes = [], Counter()
        while time.monotonic() < deadline:
            i = rnd.randint(1, args.users)
            uid = f"u{i:07d}"
            t = time.perf_counter()
            if args.workload == "bind":
                code, n = c.bind(f"uid={uid},{people}", args.password), 1
            elif args.workload == "uid":
                code, n = c.search(people, and_(and_(*gate), eq("uid", uid)))
            elif args.workload == "mail":
                code, n = c.search(people, and_(*gate, eq("mail", f"{uid}@bench.test")))
            else:
                code, n = c.search(people, and_(*gate, eq("mailAlias", f"alias{i}@bench.test")))
            mine.append(time.perf_counter() - t)
            my_codes[(code, n)] += 1
        with lock:
            latencies.extend(mine)
            codes.update(my_codes)

    threads = [threading.Thread(target=worker, args=(s,)) for s in range(args.conns)]
    start = time.monotonic()
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    elapsed = time.monotonic() - start

    if not latencies:
        raise SystemExit("no operations completed")
    ms = sorted(x * 1000 for x in latencies)
    pct = lambda p: ms[min(len(ms) - 1, int(len(ms) * p))]
    print(f"workload={args.workload} users={args.users} conns={args.conns} ops={len(ms)} "
          f"rate={len(ms) / elapsed:.0f}/s p50={statistics.median(ms):.2f}ms "
          f"p95={pct(0.95):.2f}ms p99={pct(0.99):.2f}ms max={ms[-1]:.2f}ms")
    print("results (code, entries): " + ", ".join(f"{k}x{v}" for k, v in codes.most_common()))


if __name__ == "__main__":
    main()
