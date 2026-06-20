# BMS over CAN — two-ESP setup (MCP2551)

Two ESP32 boards, each with its own MCP2551 CAN transceiver:

```
[ESP Slave]                          CAN bus                       [ESP Master]
 dummy sim                                                          decode → JSON
   │ GPIO26 TX ─────► TXD          CANH ──────twisted pair──────── CANH          TXD ◄───── TX GPIO26 │
   │ GPIO27 RX ◄─LS◄─ RXD MCP2551                                       MCP2551 RXD ─LS─► RX GPIO27 │
   │ 5V ────────────VCC            CANL ──────────────────────────  CANL          VCC─────────────5V │
   │ GND ───────────GND ───────────────────── common GND ─────────────────────  GND───────────────GND │
                                                                                  │ USB
                                                                                  ▼
                                                                            BMSMonitor (PC)
```
`LS` = level shifter (or simple resistor divider) on the RXD line — MCP2551 RXD
swings to 5 V and would otherwise feed straight into a 3.3 V-only ESP32 GPIO.

- `BMS_ESP32_Slave_CAN/` — generates dummy telemetry, broadcasts it on CAN.
- `BMS_ESP32_Master_CAN/` — receives the CAN frames, prints JSON lines on USB
  serial for BMSMonitor. (This is `esp32_bms_bridge.ino` with the decode done.)

## Wiring (per board)

| ESP32 pin | MCP2551 pin | note |
|-----------|-------------|------|
| GPIO26    | TXD         | CAN TX out of the ESP — 3.3 V into MCP2551 TXD is fine |
| GPIO27    | RXD         | CAN RX into the ESP — **MUST be level-shifted**: MCP2551 RXD swings to VCC (5 V), which exceeds the ESP32's 3.3 V GPIO max and can damage the pin |
| 5V        | VCC         | MCP2551 is a **5 V part** (not 3.3 V tolerant on VCC) |
| GND       | GND         | tie **both** boards' GND together |
| —         | Rs          | tie to GND for high-speed mode (floating/high = standby = no comms) |

Bus side: **CANH↔CANH** and **CANL↔CANL** between the two transceivers (use a
twisted pair). Put a **120 Ω** terminator at each end — most MCP2551
breakout boards already have one onboard, so with two boards you're set.

> Pins/bitrate are `#define`d at the top of each sketch. Both nodes must use
> the same values. Default bus speed: **500 kbit/s**.

## Flashing

1. Arduino IDE → install the **ESP32 (Arduino-ESP32)** board package. No extra
   libraries needed (TWAI ships with the core).
2. Open `BMS_ESP32_Slave_CAN.ino`, select the slave board + port, Upload.
3. Open `BMS_ESP32_Master_CAN.ino`, select the master board + port, Upload.
4. In BMSMonitor: Control Panel → Serial tab → pick the **master's** COM port
   → 115200 → Connect. Live data should appear.

## Verifying the link (no boot self-test)

Both sketches used to run a boot-time self-test (self-reception loopback)
before going live. It was removed: with the MCP2551's passive RXD
level-shifter, that tight self-loop frequently reported "not detected" even
though real frame exchange with the other node worked fine (the self-loop
is far more timing-sensitive than normal ACK sampling) — a false negative
that only added noise. Both nodes now go straight into normal TWAI mode at
boot.

To confirm the link is actually up, open the **slave's Serial Monitor at
115200** — it always prints a status line each cycle:

```
[slave] CAN link UP — frames are being ACKed by the master.
[slave] tx  soc=50.5%  v=71.47V  i=10.00A  st=1  bal=0x00000
```

`CAN link UP` only appears once the master is powered, wired, and actually
ACKing frames; `DOWN` means no ACK (master offline or a wiring problem). Set
`#define MASTER_DEBUG 1` in the master sketch to also see its own bus state
counters (`bus=RUNNING rx=… txErr=… rxErr=…`) in its Serial Monitor — useful
when frames aren't reaching BMSMonitor at all. Leave it `0` once everything
works: those lines are plain text and would otherwise show up as parse
errors in BMSMonitor.

> **No link / one side never goes UP:** check 5V power actually present at
> the MCP2551 VCC, **Rs pin tied to GND** (high-speed mode — floating/high =
> standby = no comms), the **RXD level-shifter** actually wired (not just
> bridged) on both boards, **TXD/RXD not swapped** (GPIO26→TXD, GPIO27→RXD),
> and **120 Ω termination** at both bus ends.
>
> **Slave reboots with `assert ... twai_handle_tx_buffer_frame ... tx_msg_count >= 0`:**
> that panic comes from transmitting into a bus where nobody ACKs (retransmit
> storm → bus-off → driver TX-count underflow). The slave transmits
> **single-shot** and clears its TX queue on bus-off, which prevents it; the
> underlying cause is still that the master node can't receive/ACK.

## CAN frame protocol (500 kbit/s, standard 11-bit IDs)

A full snapshot = 10 frames. `0x130` is sent **last** and tells the master the
snapshot is complete (that's when it emits one JSON line).

| ID | Payload | Encoding |
|----|---------|----------|
| `0x100` PACK | 7 B | `[0:1]` Vpack u16 LE ×0.01 V · `[2:3]` I i16 LE ×0.01 A (+chg/−dis) · `[4:5]` SoC u16 LE ×0.01 % · `[6]` status (0 idle, 1 charging, 2 discharging) |
| `0x110…0x114` CELLS | 8 B | 4 cells/frame, u16 LE mV (`0x110`=cells 0-3 … `0x114`=cells 16-19) |
| `0x120…0x122` TEMPS | 8/8/4 B | i16 LE ×0.1 °C (`0x120`=temps 0-3 · `0x121`=4-7 · `0x122`=8-9) |
| `0x130` BAL | 3 B | bitmask LE — bit *i* set ⇒ cell *i* balancing |

The master converts this back into the line BMSMonitor parses:

```json
{"v":74.12,"i":-10.00,"soc":78.0,"st":"discharging","cells":[…20…],"temps":[…10…],"bal":[16,17,18,19]}
```

## Troubleshooting

- **No data in BMSMonitor:** confirm you connected to the *master's* port, both
  nodes are at 500 kbit/s, and CANH/CANL aren't swapped.
- **Master serial shows nothing / CAN bus-off:** check 120 Ω termination, the
  common GND between boards, and that VCC is 5 V. Both sketches auto-recover
  from bus-off, but bad wiring keeps them there.
- **ESP32 GPIO27 (RX) acting flaky or the board resets randomly:** check the
  RXD level-shifter — MCP2551 RXD idles/swings to 5 V, and feeding that
  straight into the ESP32 GPIO is a common way to damage or destabilize it.
- **Slave debug:** open the *slave's* serial monitor at 115200 — it prints a
  `[slave] tx …` line each cycle (set `DEBUG_PRINT 0` to silence).

## Arduino Uno variant (MCP2515 + SN65HVD230)

Sketches: `BMS_UNO_Slave_CAN/` and `BMS_UNO_Master_CAN/`. Same CAN protocol and
500 kbit/s as the ESP32 sketches, so **Uno and ESP32 nodes interoperate** (e.g.
an Uno slave can feed an ESP32 master).

**The Uno has no built-in CAN controller**, so it needs an **MCP2515** (SPI) in
front of the SN65HVD230:

```
Uno --SPI--> MCP2515 (controller) --TXCAN/RXCAN--> SN65HVD230 (transceiver) --CANH/CANL--> bus
```

**Uno ↔ MCP2515 (SPI):**

| Uno | MCP2515 |
|-----|---------|
| D10 | CS |
| D13 | SCK |
| D11 | SI (MOSI) |
| D12 | SO (MISO) |
| 5V/3V3 + GND | VCC + GND |

INT is not wired — the sketches poll.

**Using the SN65HVD230 specifically:** the SN65HVD230 is 3.3 V. Two options:
- *Simplest:* use a standard MCP2515 module (it already has a 5 V transceiver,
  usually TJA1050) directly at 5 V — code is identical, but it doesn't use the
  SN65HVD230.
- *As requested:* run the MCP2515 **and** SN65HVD230 at **3.3 V**, wire MCP2515
  `TXCAN→D`, `RXCAN→R`, and **level-shift the SPI** lines from the 5 V Uno
  (SCK/MOSI/CS down to 3.3 V; MISO into the Uno is usually fine as-is). Then
  120 Ω termination at both ends + common GND, as usual.

**Library:** install **"MCP_CAN" by coryjfowler** (Library Manager). Set
`MCP_CLOCK` to match your module's crystal — `MCP_8MHZ` (blue boards, common) or
`MCP_16MHZ`. Wrong crystal = silent no-comms.

**Self-test note:** on the Uno the boot check confirms the **MCP2515 controller**
(SPI reachable + internal loopback). The SN65HVD230 itself is verified at
runtime by the link state (UP once frames are actually ACKed/received), because
MCP2515 loopback mode bypasses the transceiver.

## Reverse path (config edits) — not wired

BMSMonitor doesn't transmit parameter edits today (see the main app note). If
you later add it, the master would receive config on serial RX and forward it
to the slave as an extra CAN ID (e.g. `0x200`) — ask and it can be added.
