# Generator fuel and starter checks

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
