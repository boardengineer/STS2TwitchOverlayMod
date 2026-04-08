# STS2TwitchEBS

## Data Files

The `data/` directory contains JSON dictionaries for cards, enemies, relics, potions, powers, and intents. Each entry includes a sequential `id` integer field used in the broadcast payload to minimize message size.

These sequential IDs are assigned by the [STS2 Content Exporter mod](https://github.com/boardengineer/STS2ContentExporter) and must remain stable across exports for the overlay to interpret IDs correctly.

