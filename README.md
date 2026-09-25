# Server Chunk Backup

A client-side Vintage Story mod that captures the world data a server sends to
your client, plus an offline writer that rebuilds it into a real `.vcdbs` world save
you can open in singleplayer.

> ⚠️ **Use it only on servers you own, or where the owner has given you permission.**
> Capturing someone else's builds may break that server's rules. Everything is
> written to your local disk — the mod sends nothing anywhere.

Verified on Vintage Story **1.22.7** · .NET **10.0** · Linux.

---

## How it works

```
          multiplayer server
                 │  Packet_Server
                 ▼
      Vintage Story client ──► Harmony patch SystemNetworkProcess.ProcessInBackground
                 │
                 │  every packet from the server: chunks, light, decor, block
                 │  entities, moddata, heightmaps, block registry, world parameters
                 ▼
      <DataPath>/FullCapture/<id>/        lossless capture (capture.vscap)
                 │
                 ▼
      vsfullcapture-writer                offline → real .vcdbs
```

The mod hooks the single method every server packet passes through, so it captures
the maximum: blocks, light, light saturation, liquids, decor, **block entities**
(chiseled blocks, chests, machines), moddata, column heightmaps, the block registry
and world parameters. Region contents (`mapregion`) are not saved — the game
rebuilds them from the seed.
