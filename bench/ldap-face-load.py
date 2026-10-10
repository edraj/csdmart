#!/usr/bin/env python3
"""Load generator for dmart's LDAP directory face (Ldap/).

Standard library only: it carries its own few dozen lines of BER, so it runs
anywhere without python-ldap or ldap3. Each worker holds one connection, binds
once as a service account, then repeats one workload as fast as the server
answers, the way Postfix and Dovecot reuse a connection.

Workloads, named for the client whose query they reproduce:

  uid      Dex / Gitea login:  (&(&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail))(uid=uNNNNNNN))
  mail     Postfix mailbox:    (&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mail=uNNNNNNN@bench.test))
  alias    Postfix alias:      (&(objectClass=freexUser)(isActive=TRUE)(authorizedService=mail)(mailAlias=aliasN@bench.test))
  bind     Dovecot login:      a simple bind as uid=uNNNNNNN (pays for an Argon2 verify)
  listing  Gitea user sync:    a full PAGED listing of
                               (&(objectClass=freexUser)(isActive=TRUE)(authorizedService=gitea)(uid=*)),
                               timed per listing and per page
  http     dmart's own API:    GET /user/profile as one logged-in user, i.e. the
                               ordinary app traffic the server also carries

Users are expected to be named u0000001..uNNNNNNN, mailbox uNNNNNNN@bench.test,
alias aliasN@bench.test, as bench/ldap-face-scale.sh seeds them.

  bench/ldap-face-load.py --port 13389 --base dc=bench --users 1000000 \\
      --workload uid --conns 8 --seconds 10 [--report-every 60]
"""
import argparse
import http.client
import json
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
    return tlv(tag, s.encode() if isinstance(s, str) else s)


def integer(v, tag=0x02):
    return tlv(tag, v.to_bytes(max(1, (v.bit_length() + 8) // 8), "big", signed=True))


def seq(*parts, tag=0x30):
    return tlv(tag, b"".join(parts))


def eq(attr, value):
    return seq(octets(attr), octets(value), tag=0xA3)


def present(attr):
    return octets(attr, 0x87)


def and_(*filters):
    return seq(*filters, tag=0xA0)


def bind_request(mid, dn, password):
    return seq(integer(mid), seq(integer(3), octets(dn), octets(password, 0x80), tag=0x60))


PAGED_OID = "1.2.840.113556.1.4.319"


def search_request(mid, base, flt, attrs, page_size=None, cookie=b""):
    msg = [integer(mid), seq(
        octets(base), integer(2, 0x0A), integer(0, 0x0A), integer(0), integer(0),
        tlv(0x01, b"\x00"), flt, seq(*[octets(a) for a in attrs]), tag=0x63)]
    if page_size is not None:
        value = seq(integer(page_size), octets(cookie))
        msg.append(seq(seq(octets(PAGED_OID), octets(value)), tag=0xA0))
    return seq(*msg)


def _read_tlv(buf, i):
    tag = buf[i]
    n = buf[i + 1]
    i += 2
    if n & 0x80:
        k = n & 0x7F
        n = int.from_bytes(buf[i:i + k], "big")
        i += k
    return tag, buf[i:i + n], i + n


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
        _, _, i = _read_tlv(body, 0)            # messageID
        op_tag, op, i = _read_tlv(body, i)      # protocolOp
        controls = body[i:] if i < len(body) else b""
        return op_tag, op, controls

    @staticmethod
    def _cookie(controls):
        if not controls:
            return b""
        _, seq_of, _ = _read_tlv(controls, 0)       # [0] Controls
        i = 0
        while i < len(seq_of):
            _, control, i = _read_tlv(seq_of, i)
            _, oid, j = _read_tlv(control, 0)
            if oid.decode() != PAGED_OID:
                continue
            if control[j] == 0x01:                  # criticality
                _, _, j = _read_tlv(control, j)
            _, value, _ = _read_tlv(control, j)
            _, inner, _ = _read_tlv(value, 0)
            _, _, k = _read_tlv(inner, 0)           # size
            _, cookie, _ = _read_tlv(inner, k)
            return cookie
        return b""

    # Both responses' content starts with resultCode ENUMERATED (0a 01 NN).
    def bind(self, dn, password):
        self.mid += 1
        self.sock.sendall(bind_request(self.mid, dn, password))
        _, op, _ = self._message()
        return op[2]

    def search(self, base, flt, attrs=("mail",), page_size=None, cookie=b""):
        self.mid += 1
        self.sock.sendall(search_request(self.mid, base, flt, list(attrs), page_size, cookie))
        entries = 0
        while True:
            tag, op, controls = self._message()
            if tag == 0x64:
                entries += 1
            elif tag == 0x65:
                return op[2], entries, self._cookie(controls)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=13389)
    ap.add_argument("--base", default="dc=bench")
    ap.add_argument("--users", type=int, required=True)
    ap.add_argument("--workload", choices=["uid", "mail", "alias", "bind", "listing", "http"], required=True)
    ap.add_argument("--conns", type=int, default=1)
    ap.add_argument("--seconds", type=float, default=10)
    ap.add_argument("--report-every", type=float, default=0, help="also print one stats line per interval")
    ap.add_argument("--page-size", type=int, default=500)
    ap.add_argument("--listings", type=int, default=0, help="listing: stop after this many (0 = until --seconds)")
    ap.add_argument("--service", default="mail")
    ap.add_argument("--password", default="Bench12345")
    ap.add_argument("--http-port", type=int, default=0)
    ap.add_argument("--http-user", default="u0000001")
    ap.add_argument("--label", default="")
    args = ap.parse_args()

    people = f"ou=people,{args.base}"
    service_dn = f"cn={args.service},ou=services,{args.base}"
    gate = (eq("objectClass", "freexUser"), eq("isActive", "TRUE"), eq("authorizedService", "mail"))
    gitea_sync = and_(eq("objectClass", "freexUser"), eq("isActive", "TRUE"),
                      eq("authorizedService", "gitea"), present("uid"))

    samples, codes, listings, lock = [], Counter(), [], threading.Lock()
    start = time.monotonic()
    deadline = start + args.seconds

    def http_worker():
        c = http.client.HTTPConnection("127.0.0.1", args.http_port, timeout=30)
        c.request("POST", "/user/login", json.dumps({"shortname": args.http_user, "password": args.password}),
                  {"Content-Type": "application/json"})
        token = json.loads(c.getresponse().read())["records"][0]["attributes"]["access_token"]
        mine, my_codes = [], Counter()
        while time.monotonic() < deadline:
            t = time.perf_counter()
            c.request("GET", "/user/profile", headers={"Authorization": f"Bearer {token}"})
            r = c.getresponse()
            r.read()
            mine.append((time.monotonic() - start, time.perf_counter() - t))
            my_codes[(r.status, 1)] += 1
        with lock:
            samples.extend(mine)
            codes.update(my_codes)

    def ldap_worker(seed):
        rnd = random.Random(seed)
        c = Conn(args.host, args.port)
        if args.workload != "bind":
            code = c.bind(service_dn, args.password)
            if code != 0:
                raise SystemExit(f"service bind failed with {code}")
        mine, my_codes, my_listings = [], Counter(), []
        while time.monotonic() < deadline:
            if args.workload == "listing":
                t0, entries, pages, cookie = time.perf_counter(), 0, 0, b""
                while True:
                    t = time.perf_counter()
                    code, n, cookie = c.search(people, gitea_sync, ("uid", "mail"), args.page_size, cookie)
                    mine.append((time.monotonic() - start, time.perf_counter() - t))
                    entries += n
                    pages += 1
                    if code != 0 or not cookie:
                        my_codes[(code, "listing")] += 1
                        break
                    if time.monotonic() >= deadline:
                        my_codes[("cut by deadline", "listing")] += 1
                        break
                my_listings.append((time.perf_counter() - t0, entries, pages, not cookie))
                if args.listings and len(my_listings) >= args.listings:
                    break
                continue
            i = rnd.randint(1, args.users)
            uid = f"u{i:07d}"
            t = time.perf_counter()
            if args.workload == "bind":
                code, n = c.bind(f"uid={uid},{people}", args.password), 1
            elif args.workload == "uid":
                code, n, _ = c.search(people, and_(and_(*gate), eq("uid", uid)))
            elif args.workload == "mail":
                code, n, _ = c.search(people, and_(*gate, eq("mail", f"{uid}@bench.test")))
            else:
                code, n, _ = c.search(people, and_(*gate, eq("mailAlias", f"alias{i}@bench.test")))
            mine.append((time.monotonic() - start, time.perf_counter() - t))
            my_codes[(code, n)] += 1
        with lock:
            samples.extend(mine)
            codes.update(my_codes)
            listings.extend(my_listings)

    target = http_worker if args.workload == "http" else ldap_worker
    threads = [threading.Thread(target=target, args=() if args.workload == "http" else (s,)) for s in range(args.conns)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    elapsed = time.monotonic() - start

    if not samples:
        raise SystemExit("no operations completed")

    def line(chunk, span, prefix):
        ms = sorted(x * 1000 for x in chunk)
        pct = lambda p: ms[min(len(ms) - 1, int(len(ms) * p))]
        return (f"{prefix} ops={len(ms)} rate={len(ms) / span:.0f}/s p50={statistics.median(ms):.2f}ms "
                f"p95={pct(0.95):.2f}ms p99={pct(0.99):.2f}ms max={ms[-1]:.2f}ms")

    label = args.label or args.workload
    if args.report_every:
        buckets = {}
        for at, lat in samples:
            buckets.setdefault(int(at // args.report_every), []).append(lat)
        for k in sorted(buckets):
            print(line(buckets[k], args.report_every, f"[{label}] t={k * args.report_every:.0f}s"))
    print(line([lat for _, lat in samples], elapsed,
               f"workload={label} users={args.users} conns={args.conns}"))
    print("results (code, entries): " + ", ".join(f"{k}x{v}" for k, v in codes.most_common()))
    complete = [x for x in listings if x[3]]
    if complete:
        secs = [x[0] for x in complete]
        print(f"listing: {len(complete)} complete, {complete[0][1]} entries in {complete[0][2]} pages each, "
              f"mean {statistics.mean(secs):.1f}s max {max(secs):.1f}s"
              f"{f', plus {len(listings) - len(complete)} cut by the deadline' if len(listings) > len(complete) else ''}")
    elif listings:
        x = listings[0]
        print(f"listing: none complete; {x[1]} entries in {x[2]} pages in {x[0]:.1f}s before the deadline")


if __name__ == "__main__":
    main()
