# Multi-VLAN monitoring

A site usually has several VLANs (users, servers, management, voice, cameras). The agent can monitor all of them in one of two modes, chosen per site in the portal.

## Routed mode (default)

The agent has one IP on one VLAN. Probes to devices on other VLANs leave through the default gateway and are routed like any other inter-VLAN traffic.

- **Network change needed:** none. Plug the agent into an access port.
- **Works when:** the firewall/L3 switch permits ICMP, and the TCP/HTTP ports you check, from the agent's VLAN to the others.
- **Limits:** you only prove the device is reachable *through the router*, not that the VLAN itself is healthy; devices that drop traffic from other subnets (common for management VLANs) look down; discovery only finds hosts that answer ICMP across the router, never L2-only hosts.

Pick this for small sites, or anywhere you cannot touch switch configuration.

## Trunked mode

The agent's switch port is an 802.1Q trunk. The host creates a sub-interface per VLAN (`eth0.10`, `eth0.20`, …) with an address on each. The agent lists those interfaces in every heartbeat, and for each probe binds to the interface whose subnet contains the target (or, failing that, whose VLAN id matches the device's VLAN in the portal).

- **Proves L2 reachability** on every VLAN, independent of inter-VLAN routing and ACLs.
- **Discovery** can sweep a VLAN and read the ARP table, so printers, cameras and IoT gear that ignore cross-subnet pings still show up.
- Required for Phase 2 access into management VLANs that are not routable from the user network.

### Switch side

Allow the native VLAN (untagged, for the agent's own management address) plus the tagged VLANs you want to monitor. Keep the list tight; the agent should not be able to reach VLANs it has no business on.

Cisco IOS / IOS-XE:
```
interface GigabitEthernet1/0/24
 description nms-agent trunk
 switchport mode trunk
 switchport trunk native vlan 1
 switchport trunk allowed vlan 1,10,20,30
 spanning-tree portfast trunk
```

Aruba / HP ProCurve (AOS-S):
```
vlan 1  untagged 24
vlan 10 tagged 24
vlan 20 tagged 24
vlan 30 tagged 24
```

UniFi: edit the port profile for the agent's port, set Native VLAN to the management network and Tagged VLANs to the networks to monitor (or "Allow all" on a lab switch).

### Host side

Raspberry Pi OS (Bookworm) and Ubuntu both work. Use the helper:

```bash
# preview
./vlan-setup.sh --parent eth0 --vlan 10=dhcp --vlan 20=10.20.0.5/24 --vlan 30=dhcp --dry-run
# apply (netplan on Ubuntu/Pi OS with netplan, otherwise NetworkManager)
sudo ./vlan-setup.sh --parent eth0 --vlan 10=dhcp --vlan 20=10.20.0.5/24 --vlan 30=dhcp --apply
```

Static addresses are preferable: a DHCP lease on a VLAN that also hands out a default route can hijack the agent's path to Azure. The helper sets `use-routes: false` / `never-default yes` on DHCP sub-interfaces for that reason.

Equivalent netplan by hand (`/etc/netplan/60-nms-vlans.yaml`):
```yaml
network:
  version: 2
  vlans:
    eth0.10:
      id: 10
      link: eth0
      addresses: [10.10.0.5/24]
    eth0.20:
      id: 20
      link: eth0
      dhcp4: true
      dhcp4-overrides: { use-routes: false }
```

### Verify

```bash
ip -d link show type vlan          # eth0.10@eth0 ... vlan protocol 802.1Q id 10
ip -4 addr show                    # an address on every sub-interface
ping -c3 -I eth0.20 10.20.0.1      # gateway on VLAN 20 answers
sudo tcpdump -ni eth0 -c 5 vlan    # tagged frames leaving the trunk
cat /proc/net/vlan/config          # what the agent reads to map name -> VLAN id
```

In the portal the agent row lists each interface with its VLAN id. Set the VLAN on each device (or leave it blank and rely on subnet matching) and the agent will source probes from the right interface. `sudo tcpdump -ni eth0.20 icmp` while a check runs is the definitive proof.

## Choosing

| | Routed | Trunked |
|---|---|---|
| Switch config | none | trunk port |
| Proves | routed reachability | L2 reachability per VLAN |
| Discovery | ICMP responders only | ICMP + ARP, all hosts |
| Management VLANs blocked from users | looks down | works |
| Phase 2 SSH/HTTP into mgmt VLAN | only if routable | yes |

Start Routed where you need something today; switch sites to Trunked as you get switch access. Changing the mode is a dropdown; the agent reconfigures on its next config push.
