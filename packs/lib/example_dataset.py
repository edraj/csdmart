"""The contract a storyline module must satisfy. Copy this to start a new one.

A dataset is one module in this directory plus a value: set
`PACKS_DATASET=<module name>` (or pass `--dataset <name>` to build.sh and
install.sh) and the generators read that module instead of `shanidar`.

    PACKS_DATASET=school python3 packs/lib/gen_dataset.py --scale small
    ./packs/install.sh --dataset school

The name also seeds the UUIDs, so two storylines never collide — and changing
the name of an EXISTING dataset would re-identify every row in every install of
it, so pick it once.

Every attribute below must exist, because the generators read all of them. An
empty list is a perfectly good answer for a pack you are not populating: a
storyline that only fills `org` leaves the rest empty and the other packs
install as empty structures.

    REGIONS            region rows            sn, name, hq, code
    SITES              site rows              sn, name, region, city, kind,
                                              status, on, lat, lon, grid
    PRODUCTS TARIFFS   catalogue
    EQUIPMENT          assets                 sn, name, kind, site, status,
          MAINTENANCE                         on, kva, serviced
    ARTICLES           kb, markdown bodies    sn, title, tags, reviewed, body
    CASES              servicedesk, live      the four demo.sh drives
    CUSTOMER_CASES     owned by a customer
    INTAKE_CASES       owned by `anonymous`
    ARCHIVE_CASES      shipped WITH history   events: (day, actor, action[, reason])
    REQUESTS           approvals
    KPI_ROWS DATASETS  datamart
    NOTICES            comms
    PERSONAS           staff and customers    sn, name, roles, groups, msisdn
    EQUIPMENT_HISTORY  non-ticket history     {sn: [(day, actor, diff)]}
    RESOLUTION_CODES   closing vocabulary     [(key, label)]
    REJECTION_CODES    approvals vocabulary

Two rules that are not negotiable, because they are what keeps the demo safe to
show anyone: nothing real — no actual company, person, brand or subscriber —
and no credentials. Emails belong under `example.com` (RFC 2606) and phone
numbers must be unassignable. Demo passwords come from the environment at
install time and are never committed.
"""

# A single region and a single site: enough for the `org` pack to build, and
# enough to prove the generators are reading THIS module rather than shanidar.
REGIONS = [
    dict(sn="campus_north", name="North Campus", hq="Hall One", code="north"),
]
SITES = [
    dict(sn="bld_001", name="Science Block", region="north", city="Hall One",
         kind="macro", status="live", on="2024-01-01",
         lat=36.0, lon=44.0, grid=24),
]

# Everything else empty: the packs that read these install as bare structures.
PRODUCTS = []
TARIFFS = []
EQUIPMENT = []
MAINTENANCE = []
ARTICLES = []
CASES = []
CUSTOMER_CASES = []
INTAKE_CASES = []
ARCHIVE_CASES = []
REQUESTS = []
KPI_ROWS = []
DATASETS = []
NOTICES = []
PERSONAS = []
EQUIPMENT_HISTORY = {}

RESOLUTION_CODES = [("fixed", "Fixed")]
REJECTION_CODES = [("declined", "Declined")]
