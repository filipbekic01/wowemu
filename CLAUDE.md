# WowEmu

A .NET 10 reimplementation of AzerothCore, targeting World of Warcraft 3.3.5a (client build 12340).

## AzerothCore is the source of truth

`azerothcore-wotlk/` is a checkout of the C++ server this project reimplements. When you are unsure
how something should behave, read it there rather than guessing — it is a working emulator, and its
behaviour is the specification.

This matters most where the correct answer is not the obvious one. Prefer reproducing what upstream
does, and when you deliberately depart from it, say why.
