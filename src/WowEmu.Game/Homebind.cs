using WowEmu.Core;

namespace WowEmu.Game;

/// <summary>
/// Where a character comes back to.
/// </summary>
/// <remarks>
/// Set by an innkeeper, and defaulting to the race and class's starting point. Distinct from the
/// graveyard a ghost releases to — the graveyard is wherever you happened to die, and this is
/// somewhere you chose.
/// </remarks>
/// <param name="AreaId">
/// The <i>area</i>, not the zone, despite upstream's column being called <c>zoneId</c>. The client
/// shows the area's name on the hearthstone, so a zone here labels it with the wrong place.
/// </param>
public readonly record struct Homebind(uint MapId, uint AreaId, Position Position);
