# Points and cost

`/banditevent 200` does not mean "difficulty 200". It means **200 points to spend**. Everything
that can be spawned carries a price, the draw buys whole squads and crewed vehicles until nothing
else is affordable, and the change is spent on individual men who join the last squad.

The unit is the rifleman. Everything else is quoted against him, so if the rifleman costs 10, a
squad of five plain riflemen costs 50, a marksman worth 26 points is "worth 2.6 riflemen", and a
tank at 200 is worth twenty men. Pick what a rifleman is worth to you and the whole table follows.

Two consequences worth knowing:

- **The same budget does not buy the same event twice.** The draw is random, weighted by what the
  configuration says is ordinary. Pass `seed:<n>` to reproduce one exactly; the reply prints the
  seed it used.
- **A bigger budget unlocks *kinds* of thing, not just more of them.** A large event brings
  marksmen and armour rather than a great many riflemen.

## Where the prices live

Every price is an ordinary number in `BanditPlugin.configuration.xml`, and you can edit any of
them by hand.

| Where | What it prices |
|---|---|
| `Kits` → `<Cost>` on each kit | One bandit of that class. |
| `Vehicles` → `<Cost>` on each vehicle type | The empty vehicle. |
| `Squads` | Nothing directly - a squad type costs the sum of the kits in it. |

**A squad's price is calculated, not configured.** It is the sum of its members' kit costs, so
adding a marksman to a squad type makes that type dearer with nothing else to edit.

**A vehicle's price is its own `Cost` plus the cost of every kit in its `Crew` list.** So an
armoured truck priced at 40 carrying two riflemen and a breacher at 10/10/14 costs 74 to put on
the ground, and adding a seat to the crew re-prices it automatically.

Run **`/banditevent check`** to see what the configuration can actually draw and what everything
costs, and what is wrong with anything unpriceable.

## Having the plugin suggest prices

`/banditcost` prices every kit and vehicle from the game's own asset data and prints the working,
so a surprising number can be argued with. `/banditcost apply` writes the suggestions into the
configuration.

It is a **suggestion**, not an answer - see the two things it cannot see, below.

### The kit formula

A kit's raw threat is three things multiplied:

```
threat = (damage x rounds/sec x hit chance) x reach x armour
```

| Term | Where it comes from |
|---|---|
| **damage** | The gun asset's `Player_Damage`, times its spine multiplier (the aim model shoots centre of mass, not heads), times the magazine's pellet count - which is what makes a shotgun price as a shotgun rather than a slow rifle. |
| **rounds/sec** | **The kit's own pacing, not the gun's firerate.** A burst class fires its burst size over `BurstIntervalSeconds`; a single-shot class fires once per `FireIntervalSeconds`. The asset's firerate is only a ceiling on how fast rounds can leave inside a burst. Pricing a Snayperskya at its mechanical 3.6 rounds a second would value a marksman at six times what it delivers. |
| **hit chance** | The kit's `AimHitChance`, or the global one if the kit does not set it. |
| **reach** | The shorter of the kit's `FireRange` and the gun's own range, divided by `CostModelReachBaseline`. Reach is a separate term because a marksman and a breacher doing identical damage are not equivalent - one is shooting at you for 150m before the other can see you. |
| **armour** | The damage reduction of the kit's hat, vest, shirt and trousers, multiplied together and inverted. Tougher kit, higher threat. |

Raw threat is in units nobody has intuition for, so the whole table is then **scaled against an
anchor**: `CostModelAnchorKit` is priced at exactly `CostModelAnchorPoints`, and everything else
comes out as a multiple of it.

### The vehicle formula

```
price = (health / 100 x CostModelVehicleSoakWeight + turret output in men) x CostModelAnchorPoints
```

A vehicle is worth what it soaks plus what it shoots. Soak is its health measured against a
bandit's 100, so it comes out in men. Its turrets are priced with the same output arithmetic as a
kit's gun and converted through the anchor, so they can be added to the soak.

### Two things the model cannot see

- **Suppression.** A machinegun's worth is that it pins people down and denies ground, and none of
  that appears in damage per second. Priced on output alone the machinegunner comes out *below*
  the rifleman - arithmetically correct and tactically wrong.
- **Everything a squad does.** Patience, cover separation, how long a sighting is held between
  members - all real, none of it attached to any asset.

Which is why the output is meant to be read, argued with, and then written into the configuration
where it becomes an ordinary editable number.

## The knobs

| Key | Default | What it does |
|---|---|---|
| `CostModelAnchorKit` | `""` (uses `DefaultKit`) | The kit every other price is scaled against. |
| `CostModelAnchorPoints` | `10` | What that kit is worth. Setting your ordinary soldier to 10 makes every suggestion read as "worth 2.6 riflemen". |
| `CostModelReachBaseline` | `100` | The engagement range that counts as ordinary, in metres. Raising it flattens the difference between a marksman and a breacher; lowering it makes range the dominant term. 100 puts a rifleman at roughly 1. |
| `CostModelVehicleSoakWeight` | `1` | How much a vehicle's health counts toward its price. 1 means a vehicle that takes twenty bandits' worth of shooting to destroy is worth twenty bandits. Turn it down if your bandits fight mostly on foot; set it to 0 to price vehicles purely on their guns. |

## Caps that override the budget

A budget can be stopped short by these, whatever it could afford:

| Key | Default | What it caps |
|---|---|---|
| `EventMaxBandits` | `24` | Most bandits one event or convoy may spawn. |
| `EventVehicleCap` | `2` | Most vehicles an ordinary event may draw - it exists to keep a budget going mostly into men. |
| `ConvoyVehicleCap` | `6` | Most vehicles one convoy may draw. Separate, because a convoy is nothing but vehicles. |
