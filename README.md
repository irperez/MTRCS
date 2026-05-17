<div align="center">

```
╔╦╗╔╦╗╦═╗╔═╗╔═╗
║║║ ║ ╠╦╝║  ╚═╗
╩ ╩ ╩ ╩╚═╚═╝╚═╝
```

# MTRCS

**My Traceroute — rewritten in C#. Truly cross-platform. Blazing fast. Native AOT.**

[![CI](https://github.com/irperez/MTRCS/actions/workflows/ci.yml/badge.svg)](https://github.com/irperez/MTRCS/actions/workflows/ci.yml)
[![Release](https://github.com/irperez/MTRCS/actions/workflows/release.yml/badge.svg)](https://github.com/irperez/MTRCS/actions/workflows/release.yml)
[![Latest Release](https://img.shields.io/github/v/release/irperez/MTRCS?style=flat-square&color=brightgreen&label=latest)](https://github.com/irperez/MTRCS/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg?style=flat-square)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg?style=flat-square)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux%20%7C%20Raspberry%20Pi-lightgrey?style=flat-square)](#-installation)

</div>

---

> **MTRCS** is a from-scratch C# rewrite of the classic [`mtr`](https://www.bitwizard.nl/mtr/) network diagnostic tool.  
> It combines the functionality of `traceroute` and `ping` into a single, continuously-updating terminal display — now with native binaries for **every major platform**, zero runtime dependencies, and a clean modern codebase.

---

## 🖥️ Live Demo

```
mtrcs v1.0.0 — Tracing to google.com (142.250.80.46)

HOST                                       Loss%    Snt    Last     Avg    Best    Wrst   StDev  Jitter
 1. router.local (192.168.1.1)               0.0%    42     1.2     1.3     0.9     2.1     0.3     0.2
 2. 100.64.0.1                               0.0%    42     8.4     8.7     7.8     9.2     0.4     0.3
 3. 10.58.224.1                              0.0%    42    11.3    11.6    10.9    13.1     0.5     0.4
 4. 72.14.215.85                             0.0%    42    14.2    14.5    13.8    16.0     0.6     0.5
 5. 108.170.246.33                           0.0%    42    15.1    15.3    14.9    16.2     0.3     0.2
 6. 142.250.57.197                           0.0%    42    15.8    16.0    15.3    17.4     0.5     0.4
 7. lax17s55-in-f14.1e100.net (142.250.80.46)  0.0%  42   16.1    16.3    15.8    17.9     0.4     0.3
```

*Press `Ctrl+C` to exit. The display refreshes ~10×/sec for real-time visibility.*

---

## ✨ Features

| Feature | Details |
|---------|---------|
| 🔄 **Live mode** | Continuously-updating terminal UI, ~10 Hz refresh |
| 📊 **Report mode** | Run N cycles, print results, and exit — scriptable |
| 🌐 **Multi-protocol** | ICMP Echo (default), TCP SYN (`-T`), UDP (`-u`) |
| 🏷️ **ASN lookup** | Autonomous System Number via Team Cymru DNS (`-a`) |
| 📤 **Export** | Save results as `text`, `csv`, or `json` (`-o`/`-f`) |
| ⚡ **Native AOT** | Zero-dependency single binary — no .NET runtime needed |
| 🌍 **Cross-platform** | Windows, macOS, Linux, Raspberry Pi — one codebase |
| 🎨 **Rich ANSI UI** | Color-coded RTT/loss, alternate screen buffer, zero flicker |
| 🔬 **Precise stats** | Welford online algorithm for numerically stable mean/stdev |
| 📦 **Self-contained** | No install — just download and run |

---

## 📦 Installation

### Option 1 — Prebuilt Binaries (Recommended)

Download the latest release for your platform from [**Releases**](https://github.com/irperez/MTRCS/releases/latest):

| Platform | Download | Notes |
|----------|----------|-------|
| 🪟 Windows x64 | `mtrcs-vX.Y.Z-win-x64.zip` | Native AOT, no runtime needed |
| 🍎 macOS Apple Silicon | `mtrcs-vX.Y.Z-osx-arm64.tar.gz` | Native AOT, M1/M2/M3/M4 |
| 🐧 Linux x64 | `mtrcs-vX.Y.Z-linux-x64.tar.gz` | Native AOT |
| 🐧 Linux ARM64 | `mtrcs-vX.Y.Z-linux-arm64.tar.gz` | Native AOT, Pi 4/5 (64-bit) |
| 🫐 Raspberry Pi (ARMv7) | `mtrcs-vX.Y.Z-linux-arm.tar.gz` | Self-contained, Pi 1/2/3/Zero |

#### Linux / macOS

```bash
# Download and extract (replace X.Y.Z with the actual version)
curl -fsSL https://github.com/irperez/MTRCS/releases/latest/download/mtrcs-vX.Y.Z-linux-x64.tar.gz | tar -xz

# Make executable and install system-wide
chmod +x mtrcs
sudo mv mtrcs /usr/local/bin/

# Verify
mtrcs --version
```

> **Note for macOS:** On first launch, macOS may quarantine the binary. Remove the quarantine flag with:
> ```bash
> xattr -d com.apple.quarantine ./mtrcs
> ```

#### Windows

```powershell
# Extract the zip, then run from PowerShell or cmd
.\mtrcs.exe --version
```

> **Windows note:** Raw ICMP sockets require elevated privileges. Run your terminal as **Administrator** or use TCP/UDP probe mode (`-T` or `-u`).

#### Raspberry Pi

```bash
# ARM64 (Pi 4, Pi 5, Pi Zero 2W in 64-bit mode)
curl -fsSL https://github.com/irperez/MTRCS/releases/latest/download/mtrcs-vX.Y.Z-linux-arm64.tar.gz | tar -xz

# ARMv7 (Pi 1, Pi 2, Pi 3, Pi Zero, Pi Zero W)
curl -fsSL https://github.com/irperez/MTRCS/releases/latest/download/mtrcs-vX.Y.Z-linux-arm.tar.gz | tar -xz

chmod +x mtrcs
sudo mv mtrcs /usr/local/bin/
```

---

### Option 2 — Build from Source

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
git clone https://github.com/irperez/MTRCS.git
cd MTRCS/MTRCSLib

# Run the console app directly
dotnet run --project MTRCS -- google.com

# Publish a self-contained binary for your current platform
dotnet publish MTRCS/MTRCS.csproj -c Release --self-contained -r linux-x64 -o out/
./out/mtrcs google.com
```

---

## 🚀 Usage

### Basic Syntax

```
mtrcs <host> [options]
```

### Live Mode (default)

Starts a continuously-updating display, refreshing ~10 times per second.  
Press **`Ctrl+C`** to stop.

```bash
# Trace to a hostname
mtrcs google.com

# Trace to an IP
mtrcs 8.8.8.8

# Custom hop limit and interval
mtrcs example.com --max-hops 20 --interval 500

# Include ASN information (who owns each hop's IP)
mtrcs cloudflare.com --asn
```

### Report Mode

Run a fixed number of probe cycles, print the final statistics, then exit.  
Ideal for scripting, monitoring, and automation.

```bash
# Run 10 cycles (default) and print report
mtrcs google.com --report

# Run 30 cycles for a more statistically meaningful result
mtrcs 8.8.8.8 --report --cycles 30

# Save report to a file
mtrcs google.com --report --output /tmp/report.txt

# Export as CSV
mtrcs google.com --report --output results.csv --format csv

# Export as JSON (great for ingestion into dashboards)
mtrcs google.com --report --output results.json --format json
```

#### Sample JSON output

```json
{
  "host": "google.com",
  "target": "142.250.80.46",
  "generated": "2026-05-17T04:00:00+00:00",
  "hops": [
    {
      "hop": 1,
      "ip": "192.168.1.1",
      "host": "router.local",
      "loss": 0.0,
      "snt": 10,
      "last": 1.2,
      "avg": 1.3,
      "best": 0.9,
      "wrst": 2.1,
      "stDev": 0.3,
      "jitter": 0.2
    }
  ]
}
```

---

## ⚙️ Options Reference

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `<host>` | — | *(required)* | Hostname or IPv4 address to trace |
| `--max-hops` | `-m` | `30` | Maximum TTL hops (1–255) |
| `--interval` | `-i` | `1000` | Probe cycle interval in milliseconds |
| `--timeout` | `-t` | `800` | Per-probe timeout in milliseconds |
| `--size` | `-s` | `28` | ICMP payload size in bytes |
| `--report` | `-r` | `false` | Enable report mode (run N cycles and exit) |
| `--cycles` | `-c` | `10` | Number of cycles to run in report mode |
| `--asn` | `-a` | `false` | Show ASN column (Team Cymru DNS lookup) |
| `--tcp` | `-T` | `false` | Use TCP SYN probes instead of ICMP |
| `--udp` | `-u` | `false` | Use UDP probes instead of ICMP |
| `--port` | `-P` | `80`/`33434` | Destination port for TCP/UDP probes |
| `--output` | `-o` | — | File path for report export (requires `--report`) |
| `--format` | `-f` | `text` | Export format: `text`, `csv`, `json` |
| `--version` | — | — | Show version and exit |
| `--help` | `-h` | — | Show help and exit |

---

## 📊 Understanding the Output

```
HOST                                       Loss%    Snt    Last     Avg    Best    Wrst   StDev  Jitter
 1. router.local (192.168.1.1)               0.0%    42     1.2     1.3     0.9     2.1     0.3     0.2
```

| Column | Meaning |
|--------|---------|
| **HOST** | Hop number, reverse-DNS hostname, and IP address |
| **Loss%** | Percentage of probes that received no response |
| **Snt** | Total probes sent to this hop |
| **Last** | RTT of the most recent probe (ms) |
| **Avg** | Running arithmetic mean of all RTT samples (ms) |
| **Best** | Minimum RTT ever observed (ms) |
| **Wrst** | Maximum RTT ever observed (ms) |
| **StDev** | Standard deviation of RTT — indicates jitter/instability (ms) |
| **Jitter** | Absolute difference between the last two RTT samples (ms) |
| **ASN** | Autonomous System Number and name (with `--asn`) |

### Reading the Numbers

- **High Loss% at intermediate hops** — Routers often rate-limit ICMP. If the *final destination* shows 0% loss, intermediate loss is usually benign.
- **High StDev / Wrst** — Indicates bursty congestion or route instability.
- **Rising Avg over time** — Sustained congestion or a degrading link.
- **??? in RTT columns** — No successful probes received yet for that hop.

---

## 🌐 Probe Modes

MTRCS supports three probe protocols, letting you work around firewalls that block ICMP.

### ICMP (default)

Standard ICMP Echo probes — the same as traditional `ping` and `mtr`.

```bash
mtrcs google.com
```

> Requires raw socket privileges. On Linux/macOS: run with `sudo` or set `CAP_NET_RAW`. On Windows: run as Administrator.

### TCP SYN (`--tcp` / `-T`)

Sends TCP SYN packets to the destination port. Works through firewalls that permit outbound TCP.

```bash
# Default port: 80
mtrcs example.com --tcp

# Custom port (e.g., HTTPS)
mtrcs example.com --tcp --port 443
```

### UDP (`--udp` / `-u`)

Sends UDP packets. Compatible with many corporate firewalls and VPN environments.

```bash
# Default port: 33434
mtrcs example.com --udp

# Custom port
mtrcs example.com --udp --port 33434
```

> **Note:** `--tcp` and `--udp` are mutually exclusive.

---

## 🔍 ASN Lookup (`--asn`)

The `--asn` flag enriches each hop with its Autonomous System Number and description, powered by a DNS lookup against [Team Cymru's](https://www.team-cymru.com/ip-asn-mapping) IP-to-ASN mapping service.

```bash
mtrcs 8.8.8.8 --asn
```

```
HOST                                       Loss%    Snt    Last     Avg    Best    Wrst   StDev  Jitter  ASN
 1. router.local (192.168.1.1)               0.0%    42     1.2     1.3     0.9     2.1     0.3     0.2  ...
 2. 100.64.0.1                               0.0%    42     8.4     8.7     7.8     9.2     0.4     0.3  AS7922 COMCAST
 3. 96.110.41.181                            0.0%    42    11.1    11.4    10.7    13.0     0.5     0.4  AS7922 COMCAST
 4. 68.86.143.97                             0.0%    42    14.0    14.3    13.6    15.8     0.6     0.5  AS7922 COMCAST
 5. 108.170.246.33                           0.0%    42    15.2    15.4    14.9    16.2     0.3     0.2  AS15169 GOOGLE
 6. 142.250.57.197                           0.0%    42    15.8    16.0    15.3    17.4     0.5     0.4  AS15169 GOOGLE
 7. dns.google (8.8.8.8)                     0.0%    42    16.0    16.2    15.7    17.8     0.4     0.3  AS15169 GOOGLE
```

---

## 🛠️ Scripting & Automation Examples

```bash
# Run in report mode and capture exit code (0 = success)
mtrcs 8.8.8.8 --report --cycles 5
echo "Exit: $?"

# Pipe JSON output to jq for analysis
mtrcs google.com --report --cycles 20 --output /dev/stdout --format json 2>/dev/null \
  | jq '.hops[] | select(.loss > 5) | {hop, host, loss}'

# Cron-friendly: save timestamped report
mtrcs 8.8.8.8 --report --cycles 30 \
  --output "/var/log/network/mtrcs-$(date +%Y%m%d-%H%M%S).csv" \
  --format csv

# Network health check script
mtrcs 1.1.1.1 --report --cycles 10 --output report.json --format json
LOSS=$(jq '[.hops[-1].loss] | .[0]' report.json)
if (( $(echo "$LOSS > 5" | bc -l) )); then
  echo "⚠️  High loss detected: ${LOSS}%"
fi
```

---

## 🔒 Permissions

| Platform | ICMP | TCP/UDP |
|----------|------|---------|
| Linux | `sudo` or `CAP_NET_RAW` capability | No extra permissions needed |
| macOS | `sudo` | No extra permissions needed |
| Windows | Run as Administrator | No extra permissions needed |
| Raspberry Pi | `sudo` or `CAP_NET_RAW` | No extra permissions needed |

### Linux: Grant CAP_NET_RAW (avoid sudo)

```bash
sudo setcap cap_net_raw+ep /usr/local/bin/mtrcs
mtrcs google.com  # no sudo needed
```

---

## 🏗️ Architecture

MTRCS is structured as three projects in one solution:

```
MTRCSLib/
├── MTRCSLib/          # Core library — probing engine, stats, DNS, ASN
│   ├── TracerouteSession.cs     # Orchestrates the probe loop
│   ├── SystemPinger.cs          # ICMP prober (raw sockets)
│   ├── TcpPinger.cs             # TCP SYN prober
│   ├── UdpPinger.cs             # UDP prober
│   ├── HopStats.cs              # Per-hop statistics (Welford algorithm)
│   ├── CymruAsnResolver.cs      # Team Cymru ASN lookup
│   └── TracerouteOptions.cs     # Immutable configuration
│
├── MTRCS/             # Console application (CLI entry point)
│   ├── Program.cs               # Spectre.Console CLI setup
│   ├── MtrCommand.cs            # Main command — live & report modes
│   ├── MtrAnsiRenderer.cs       # Zero-alloc live ANSI renderer
│   ├── MtrRenderer.cs           # Static table renderer for report mode
│   └── ReportExporter.cs        # Export to text/CSV/JSON
│
└── MTRCSLib.Tests/    # xUnit test suite
```

### Key Design Decisions

- **Native AOT** — Compiled to native machine code. No JIT, no runtime, instant startup, minimal memory.
- **Zero-alloc hot path** — The live renderer uses pre-encoded UTF-8 byte arrays, `stackalloc` number buffers, and a single ring buffer flush per frame.
- **Welford's online algorithm** — Numerically stable mean and variance without accumulating all samples.
- **Spectre.Console CLI** — Declarative, self-documenting command definitions with built-in validation.

---

## 🤝 Contributing

Contributions are welcome! Whether it's a bug report, a feature request, or a pull request.

1. Fork the repository
2. Create a feature branch: `git checkout -b feature/my-feature`
3. Make your changes and add tests
4. Run the test suite: `cd MTRCSLib && dotnet test`
5. Push and open a Pull Request

### Development Setup

```bash
git clone https://github.com/irperez/MTRCS.git
cd MTRCS/MTRCSLib

# Build everything
dotnet build MTRCSLib.slnx

# Run tests
dotnet test MTRCSLib.slnx

# Run the console app (ICMP needs sudo on Linux/macOS)
sudo dotnet run --project MTRCS -- google.com
```

---

## 📋 Comparison with MTR

| Feature | `mtr` (original) | **MTRCS** |
|---------|-------------------|-----------|
| Platform | Linux/macOS only | Windows, macOS, Linux, Raspberry Pi |
| Runtime | C binary | Native AOT binary (no runtime) |
| Protocol | ICMP, UDP, TCP | ICMP, TCP SYN, UDP |
| Live display | ✅ | ✅ |
| Report mode | ✅ | ✅ |
| JSON export | ❌ | ✅ |
| CSV export | ❌ | ✅ |
| ASN lookup | ✅ | ✅ (Team Cymru) |
| Jitter column | ❌ | ✅ |
| Open source | GPL-2.0 | MIT |

---

## 📜 License

[MIT](LICENSE) — Copyright © 2026 Ivan R. Perez

---

<div align="center">

**MTRCS** · Built with ❤️ and C# · [Releases](https://github.com/irperez/MTRCS/releases) · [Issues](https://github.com/irperez/MTRCS/issues)

*If MTRCS helped you diagnose a network issue, consider giving it a ⭐*

</div>
