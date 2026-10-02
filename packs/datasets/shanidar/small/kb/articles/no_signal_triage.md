# Triaging a no-signal report

Work the checks in order. Most reports resolve at step 2 or 3 without a site
visit, and the order below is cheapest-first.

## 1. Confirm the scope

Ask whether the problem follows the handset or stays at one place. A fault that
travels with the customer is a SIM or device fault; one that stays put is a
coverage or site fault.

| Symptom | Most likely cause | Next step |
| --- | --- | --- |
| No service anywhere | SIM or account | Check account status, then re-seat the SIM |
| No service at one address | Site or coverage | Check the site's recent KPIs |
| Service but no data | Tariff or APN | Confirm the tariff is active |
| Intermittent at one address | Site power | Look for recent outage minutes |

## 2. Check the account

A barred or expired account presents exactly like a coverage fault. Confirm the
account is active and the tariff has not lapsed before looking at the network.

## 3. Check the serving site

Find the site covering the customer's address and read its last month of KPIs.
Availability below 99% or any outage minutes in the period usually explains the
report on its own — and means other customers on the same site will call too.

If the site shows a power fault, say so plainly: give the customer the expected
restoration window rather than asking them to reboot the handset again.

## 4. Escalate

Escalate to the field team when the site's own KPIs look healthy but the
customer still has no service at that address. Attach the site shortname and
the customer's reported times to the case before you escalate — the field team
cannot act on "no signal in Erbil".

## Closing

Close with the resolution code that matches what actually fixed it. A case
closed `fixed_remotely` when a technician replaced a generator makes the next
month's reporting wrong.
