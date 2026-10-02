#!/usr/bin/env python3
r"""The Shanidar Telecom storyline, as data.

Shanidar Telecom is a FICTIONAL Iraqi mobile operator. Nothing here is real:
no real company, brand, person or subscriber. Emails are @example.com and every
MSISDN follows the synthetic `+964 7X0 000 NNNN` pattern, which is not
assignable on any live Iraqi network. Stored digits-only (`+9647500001842`)
because dmart validates msisdn as `^\+?[0-9]{6,15}$`
(Config/RegexPatternsConfig.cs:26); the spaced form is for prose only. Real operators (Zain Iraq, Asiacell,
Korek) are deliberately absent.

Shortnames use underscores, never hyphens: dmart's shortname regex is
`^[a-zA-Zء-ي0-9٠-٩ً-ٟ_]{1,64}$` (Services/CsvService.cs:204).

The three anchors the demo walkthrough and DEMO.md are written around:

  erb_0142   Erbil, north    — a macro site with a faulty generator
  bsr_0031   Basra, south    — a macro site, the southern comparison
  krb_0007   Karbala, central— a COW deployed for the Arbaeen pilgrimage

Every cross-entity edge appears TWICE: once as a real `relationships` entry so
the graph is traversable, and once as a scalar key in `payload.body` so it can
be filtered. That is not redundancy for its own sake — relationship filtering
does not work (`@relationships[]...` is silently dropped from the WHERE clause
and the query returns unfiltered rows; PLAN.md verification 2), so the scalar
key is the only one a query can use.
"""

# The closing-reason catalogue. ONE definition, read by three places: the
# `case` schema's enum, each closing state's `resolutions[]` list, and DEMO.md.
#
# Worth being blunt about what enforces what. The workflow's
# `resolution_required: true` enforces that a top-level `resolution_reason` is
# PRESENT. Nothing in dmart enforces that its VALUE is from this list:
# `resolutions[]` is never read by the engine (zero C# references — it is
# consumed by the admin UI only) and `allowed_fields_values` is inert on the
# `progress_ticket` action. Both measured; see PLAN.md verification 3. A
# deployment that needs the value enforced needs a plugin.
RESOLUTION_CODES = [
    ("fixed_remotely",      "Fixed remotely"),
    ("site_repaired",       "Site repaired"),
    ("equipment_replaced",  "Equipment replaced"),
    ("billing_corrected",   "Billing corrected"),
    ("customer_educated",   "Customer advised"),
    ("duplicate",           "Duplicate report"),
    ("not_reproducible",    "Not reproducible"),
    ("withdrawn",           "Withdrawn by customer"),
]

REJECTION_CODES = [
    ("no_escort_available", "No escort available"),
    ("window_clashes",      "Clashes with another visit"),
    ("insufficient_detail", "Insufficient detail"),
    ("not_authorised",      "Requester not authorised for this site"),
]

REGIONS = [
    dict(sn="north",   name="North",   hq="Erbil",   code="north"),
    dict(sn="central", name="Central", hq="Baghdad", code="central"),
    dict(sn="south",   name="South",   hq="Basra",   code="south"),
]

SITES = [
    dict(sn="erb_0142", name="Erbil Citadel East", region="north", city="Erbil",
         kind="macro", status="live", on="2021-04-12", lat=36.1911, lon=44.0092, grid=18),
    dict(sn="bsr_0031", name="Basra Corniche", region="south", city="Basra",
         kind="macro", status="live", on="2020-11-03", lat=30.5085, lon=47.7804, grid=11),
    dict(sn="krb_0007", name="Karbala Arbaeen COW", region="central", city="Karbala",
         kind="cow", status="live", on="2026-08-20", lat=32.6160, lon=44.0249, grid=9),
    dict(sn="bgd_0210", name="Baghdad Karrada Rooftop", region="central", city="Baghdad",
         kind="rooftop", status="live", on="2019-06-28", lat=33.3024, lon=44.4211, grid=14),
    dict(sn="mos_0088", name="Mosul Left Bank", region="north", city="Mosul",
         kind="shelter", status="maintenance", on="2022-02-14", lat=36.3489, lon=43.1577, grid=13),
]

PRODUCTS = [
    dict(sn="prepaid_basic", name="Shanidar Prepaid Basic", family="prepaid",
         status="active", on="2019-01-15",
         summary="Pay-as-you-go voice and data on a monthly top-up."),
    dict(sn="postpaid_pro", name="Shanidar Postpaid Pro", family="postpaid",
         status="active", on="2020-03-01",
         summary="Billed monthly, with a shared data allowance across two SIMs."),
    dict(sn="home_wireless", name="Shanidar Home Wireless", family="fixed_wireless",
         status="active", on="2023-09-10",
         summary="Fixed-wireless broadband over a bundled indoor router."),
    dict(sn="iot_fleet", name="Shanidar IoT Fleet", family="iot",
         status="active", on="2024-05-20",
         summary="Low-bandwidth SIMs for vehicle and meter telemetry."),
]

TARIFFS = [
    dict(sn="prepaid_5k",   name="Prepaid 5,000",     product="prepaid_basic",
         iqd=5000,  gb=3,   mins=120,  status="active"),
    dict(sn="prepaid_15k",  name="Prepaid 15,000",    product="prepaid_basic",
         iqd=15000, gb=12,  mins=400,  status="active"),
    dict(sn="postpaid_25k", name="Postpaid 25,000",   product="postpaid_pro",
         iqd=25000, gb=30,  mins=1200, status="active"),
    dict(sn="home_40k",     name="Home Wireless 40,000", product="home_wireless",
         iqd=40000, gb=250, mins=0,    status="active"),
    dict(sn="iot_2k",       name="IoT 2,000",         product="iot_fleet",
         iqd=2000,  gb=0.5, mins=0,    status="active"),
]

EQUIPMENT = [
    dict(sn="gen_erb_0142_a", name="Erbil 0142 primary generator", kind="generator",
         site="erb_0142", status="faulty", on="2021-04-12", kva=60, serviced="2026-09-18"),
    dict(sn="cab_erb_0142_1", name="Erbil 0142 outdoor cabinet", kind="cabinet",
         site="erb_0142", status="in_service", on="2021-04-12", kva=0, serviced="2026-06-02"),
    dict(sn="cow_krb_0007",   name="Karbala Arbaeen COW unit", kind="cow",
         site="krb_0007", status="in_service", on="2026-08-20", kva=25, serviced="2026-09-25"),
    dict(sn="gen_bsr_0031_a", name="Basra 0031 primary generator", kind="generator",
         site="bsr_0031", status="in_service", on="2020-11-03", kva=80, serviced="2026-08-11"),
    dict(sn="bat_bsr_0031_1", name="Basra 0031 battery bank", kind="battery_bank",
         site="bsr_0031", status="standby", on="2020-11-03", kva=0, serviced="2026-07-30"),
    dict(sn="cab_bgd_0210_1", name="Baghdad 0210 rooftop cabinet", kind="cabinet",
         site="bgd_0210", status="in_service", on="2019-06-28", kva=0, serviced="2026-05-19"),
    dict(sn="acn_mos_0088_1", name="Mosul 0088 shelter aircon", kind="aircon",
         site="mos_0088", status="faulty", on="2022-02-14", kva=0, serviced="2026-09-01"),
]

MAINTENANCE = [
    dict(sn="mnt_erb_0142_202609", site="erb_0142", kind="reactive",
         on="2026-09-18", tech="tech_north_erbil", outcome="partial",
         equipment=["gen_erb_0142_a"],
         summary="Generator failed to start on grid loss",
         notes="Starter motor cranks but will not fire. Fuel polished, filter "
               "replaced, fault persists. Replacement starter ordered; site is "
               "on battery reserve until it arrives."),
    dict(sn="mnt_krb_0007_arbaeen", site="krb_0007", kind="installation",
         on="2026-08-20", tech="tech_north_erbil", outcome="completed",
         equipment=["cow_krb_0007"],
         summary="Arbaeen COW deployed and commissioned",
         notes="COW positioned on the eastern approach road, aligned to the "
               "pilgrim corridor. Commissioned on generator with a 9-hour grid "
               "window. Scheduled for recovery after the pilgrimage."),
    dict(sn="mnt_bsr_0031_202608", site="bsr_0031", kind="scheduled",
         on="2026-08-11", tech="tech_south_basra", outcome="completed",
         equipment=["gen_bsr_0031_a", "bat_bsr_0031_1"],
         summary="Quarterly power inspection",
         notes="Generator load-tested at 70% for one hour. Battery bank holds "
               "to specification. No action required."),
]
# `kind` above must match the equipment/maintenance_visit schema enum —
# installation is not in it, so normalise here rather than widening the schema.
for _m in MAINTENANCE:
    if _m["kind"] == "installation":
        _m["kind"] = "upgrade"

ARTICLES = [
    dict(sn="no_signal_triage", title="Triaging a no-signal report",
         tags=["agent", "coverage", "triage"], reviewed="2026-07-14",
         body="""# Triaging a no-signal report

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
"""),
    dict(sn="generator_fuel_check", title="Generator fuel and starter checks",
         tags=["technician", "power", "generator"], reviewed="2026-08-02",
         body="""# Generator fuel and starter checks

For a site generator that will not start on grid loss. Work through in order
and record what you find on the maintenance visit, including the checks that
passed — a later visit needs to know what was already ruled out.

## Safety first

Isolate the generator before opening any panel. Do not work alone on a site
with a live cabinet. If the shelter is above 45 °C, ventilate before entering.

## 1. Fuel

Check level, then quality. Water and sediment collect in the tank over a long
standby period and present as a crank-but-no-fire fault.

- Level above the 25% mark
- No water at the drain cock
- Filter clean and seated
- Lines free of air

## 2. Starting

If fuel is good and the engine still will not fire:

- **Cranks but does not fire** — fuel delivery or the starter itself
- **Does not crank** — battery, solenoid or the starter motor
- **Fires then stops** — fuel starvation or an overspeed trip

A starter that cranks strongly but never fires, with clean fuel, needs
replacing. Order the part and leave the site on battery reserve rather than
repeatedly cranking, which flattens the start battery and turns one fault into
two.

## 3. Record it

Set the equipment status to `faulty` before you leave, not after the part
arrives. The service desk reads that status when a customer calls about the
same site, and a stale `in_service` sends an agent down the wrong path.
"""),
    dict(sn="home_router_setup", title="Setting up the home wireless router",
         tags=["customer", "device", "home_wireless"], reviewed="2026-06-21",
         body="""# Setting up the home wireless router

For the indoor router bundled with Shanidar Home Wireless.

## Placing it

Signal matters more than tidiness. A router in a cupboard or behind a metal
shelf will show full bars and still run slowly.

1. Put it near an outside-facing window
2. Keep it at least two metres from a microwave or cordless phone base
3. Stand it upright — the antennas are vertical inside the case
4. Leave the indicator panel visible, so you can read it when you call us

## The indicator panel

| Light | Meaning |
| --- | --- |
| Solid green | Connected and working |
| Slow blue blink | Searching for the network |
| Solid amber | Connected, weak signal — try moving it |
| Red | No SIM detected, or the account is not active |

## If it is slow

Check the amber light first: a weak-signal connection is the most common cause
and moving the router usually fixes it. If the light is solid green and it is
still slow, note the time of day — congestion in the evening peak looks like a
fault but is not one, and we can see it in the site's figures.

## If it is red

A red light means the router cannot use the SIM. Re-seat the SIM once. If it
stays red, the account is probably not active yet; call us rather than
continuing to restart the router.
"""),
    dict(sn="cow_deployment", title="Deploying a COW for a seasonal event",
         tags=["technician", "deployment", "capacity"], reviewed="2026-08-18",
         body="""# Deploying a COW for a seasonal event

A cell on wheels covers a short, dense, predictable crowd — a pilgrimage
corridor, a festival ground — where the permanent network was never sized for
the load.

## Siting

Place the unit where the crowd will be, not where the road is convenient. A COW
200 metres off the walking route carries a fraction of the traffic it would on
the route itself.

- Clear line of sight along the corridor
- Outside the crowd's own footprint, so it stays accessible
- Reachable by the fuel bowser without crossing the route
- Far enough from the permanent site that the two do not simply overlap

## Power

Assume generator for the whole deployment and plan refuelling before you
commission. Confirm the daily grid window and size the fuel run to the gap, not
to the average — a nine-hour grid window means fifteen hours on fuel.

## Commissioning

Record the unit as equipment at the event's site, not at the nearest permanent
one. The service desk reads the site to answer capacity complaints, and a COW
recorded in the wrong place makes a busy corridor look like an empty one.

## Recovery

Agree the recovery date at deployment and record it on the visit. A COW left in
place after the crowd has gone is capacity that some other event needs.
"""),
]

# One case per interesting path through the workflow. `state` is the state the
# ticket should END in; the install walkthrough drives the transitions.
CASES = [
    dict(sn="case_000101", title="No signal at home since Tuesday evening",
         category="no_signal", severity="high", region="north",
         site="erb_0142", product="prepaid_basic", equipment="gen_erb_0142_a",
         article="no_signal_triage", msisdn="+9647500001842",
         on="2026-09-19", reporter="Dara",
         comments=[
             ("agent_baghdad", "Customer reports total loss of service at a "
              "fixed address from Tuesday evening. Service works when they "
              "travel, so this is not the handset."),
             ("agent_baghdad", "Site erb_0142 shows 41 outage minutes and the "
              "primary generator is marked faulty after the 18 Sep visit. "
              "Linking the case to both and giving the customer the "
              "restoration window."),
         ]),
    dict(sn="case_000102", title="Charged twice for the September top-up",
         category="billing", severity="normal", region="central",
         site="bgd_0210", product="postpaid_pro", equipment=None,
         article=None, msisdn="+9647700000233",
         on="2026-09-22", reporter="Nasreen",
         comments=[
             ("agent_baghdad", "Two identical charges on the same day. Raising "
              "with billing for a reversal."),
         ]),
    dict(sn="case_000103", title="Home router slow every evening",
         category="slow_data", severity="normal", region="south",
         site="bsr_0031", product="home_wireless", equipment=None,
         article="home_router_setup", msisdn="+9647800000917",
         on="2026-09-25", reporter="Hussein",
         comments=[
             ("agent_baghdad", "Solid green light, so the link is up. Slowness "
              "is confined to the evening peak, which matches the site's own "
              "figures rather than a fault."),
             ("sup_south", "Agreed — this is congestion at bsr_0031, not a "
              "fault. Sent the customer the placement guide and flagged the "
              "site for the capacity review."),
         ]),
    dict(sn="case_000104", title="Unusable data along the Arbaeen route",
         category="capacity", severity="critical", region="central",
         site="krb_0007", product="prepaid_basic", equipment="cow_krb_0007",
         article="cow_deployment", msisdn="+9647500003301",
         on="2026-08-29", reporter="Zainab",
         comments=[
             ("agent_baghdad", "Several reports along the same stretch of the "
              "eastern approach. Capacity, not coverage."),
             ("sup_south", "COW krb_0007 is commissioned and carrying load. "
              "Closing the individual reports against the deployment."),
         ]),
]

REQUESTS = [
    dict(sn="req_erb_0142_starter", site="erb_0142", region="north",
         by="tech_north_erbil", on="2026-09-26", reason="maintenance",
         escort=True,
         summary="Site access to replace the erb_0142 generator starter"),
    dict(sn="req_krb_0007_recovery", site="krb_0007", region="central",
         by="tech_north_erbil", on="2026-10-12", reason="maintenance",
         escort=False,
         summary="Site access to recover the Arbaeen COW after the pilgrimage"),
]

# Site-month KPIs. erb_0142's September dip is the generator fault, krb_0007's
# August is the COW's first partial month — the numbers tell the same story the
# cases do.
KPI_ROWS = [
    dict(site="erb_0142", period="2026_09", avail=97.2, drops=1.9, tb=41.3, outage=41),
    dict(site="erb_0142", period="2026_08", avail=99.8, drops=0.4, tb=38.1, outage=3),
    dict(site="bsr_0031", period="2026_09", avail=99.6, drops=0.6, tb=52.7, outage=9),
    dict(site="bsr_0031", period="2026_08", avail=99.7, drops=0.5, tb=50.2, outage=7),
    dict(site="krb_0007", period="2026_09", avail=99.9, drops=0.3, tb=88.4, outage=1),
    dict(site="krb_0007", period="2026_08", avail=99.1, drops=0.8, tb=22.6, outage=12),
    dict(site="bgd_0210", period="2026_09", avail=99.5, drops=0.7, tb=61.8, outage=14),
    dict(site="mos_0088", period="2026_09", avail=96.4, drops=2.4, tb=19.2, outage=96),
]

DATASETS = [
    dict(sn="site_kpis_2026_09", title="Site KPIs, September 2026",
         period="2026_09", fmt="csv",
         note="One row per site for the month. Shipped as a downloadable CSV "
              "AND mirrored as kpi_row entries under /kpis, because dmart "
              "serves the file but does not run SQL inside it."),
]

NOTICES = [
    dict(sn="erb_0142_power_works", title="Planned power work at Erbil Citadel East",
         channel="sms", audience="region", region="north", site="erb_0142",
         article=None, on="2026-09-28", status="sent"),
    dict(sn="arbaeen_capacity", title="Extra capacity along the Arbaeen route",
         channel="app_push", audience="region", region="central", site="krb_0007",
         article="cow_deployment", on="2026-08-21", status="sent"),
]

# Personas. The roles each one holds DIRECTLY — a workflow gate reads
# user.Roles and does not follow group membership (PLAN.md finding 4), so a
# persona that drives a transition must hold the gating role itself.
#
# PLAN.md also named `acct_mgr_dealers` and `backoffice_channel` to reuse the
# seeded `channel` workflow's gates. They are dropped: that workflow gates on
# roles `account_manager` and `backoffice` which dmart does NOT seed, so
# "reusing" it would mean inventing the roles anyway. The approvals pack
# defines its own workflow with <pack>_ prefixed gates instead.
PERSONAS = [
    dict(sn="agent_baghdad", name="Service desk agent, Baghdad",
         roles=["servicedesk_agent"], groups=["org_region_central"],
         msisdn="+9647700000101"),
    dict(sn="sup_south", name="Service desk supervisor, South",
         roles=["servicedesk_supervisor"], groups=["org_region_south"],
         msisdn="+9647800000102"),
    dict(sn="tech_north_erbil", name="Field technician, Erbil",
         roles=["assets_technician"], groups=["org_region_north"],
         msisdn="+9647500000103"),
    dict(sn="tech_south_basra", name="Field technician, Basra",
         roles=["assets_technician"], groups=["org_region_south"],
         msisdn="+9647800000104"),
    dict(sn="security_officer", name="Site security officer",
         roles=["approvals_security"], groups=["org_region_central"],
         msisdn="+9647700000105"),
    dict(sn="kb_author_najaf", name="Knowledge base author",
         roles=["kb_author"], groups=["org_region_central"],
         msisdn="+9647700000106"),
    dict(sn="analyst_hq", name="Network analyst, HQ",
         roles=["datamart_analyst"], groups=["org_region_central"],
         msisdn="+9647700000107"),
    dict(sn="editor_hq", name="Catalogue editor, HQ",
         roles=["catalogue_editor"], groups=["org_region_central"],
         msisdn="+9647700000108"),
]
