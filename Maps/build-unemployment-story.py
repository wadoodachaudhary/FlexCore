#!/usr/bin/env python3
"""Rebuild the bundled geography and the unemployment map story.

Reads Natural Earth and Eurostat GISCO boundaries plus the World Bank, Eurostat
and BLS extracts already downloaded under /tmp/mapdata (see the module docstring
in the story JSON "sources" for the live endpoints). Re-run after refreshing
those extracts. Writes:

  wwwroot/maps/geo/countries.geojson
  wwwroot/maps/geo/regions.geojson
  wwwroot/maps/stories/unemployment.json

Numbers are copied from those publications and rounded to 0.1. Nothing in this
script invents a rate. A country or region with no published figure is omitted
from the value list and draws grey.
"""

from __future__ import annotations

import json
from collections import defaultdict
from decimal import Decimal, ROUND_HALF_UP
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SRC = Path("/tmp/mapdata")
OUT_GEO = ROOT / "wwwroot" / "maps" / "geo"
OUT_STORY = ROOT / "wwwroot" / "maps" / "stories"

EUROPE_RATES = {
    "FI": ("Finland", 10.3),
    "ES": ("Spain", 10.0),
    "SE": ("Sweden", 8.7),
    "FR": ("France", 8.2),
    "EE": ("Estonia", 8.0),
    "GR": ("Greece", 7.4),
    "LV": ("Latvia", 7.3),
    "LU": ("Luxembourg", 7.3),
    "AT": ("Austria", 6.7),
    "RO": ("Romania", 6.4),
    "IT": ("Italy", 6.2),
    "LT": ("Lithuania", 6.1),
    "BE": ("Belgium", 5.9),
    "DK": ("Denmark", 5.9),
    "IS": ("Iceland", 5.8),
    "PT": ("Portugal", 5.7),
    "SK": ("Slovakia", 5.7),
    "CH": ("Switzerland", 5.1),
    "IE": ("Ireland", 5.0),
    "GB": ("UK", 4.9),
    "HU": ("Hungary", 4.7),
    "NO": ("Norway", 4.5),
    "HR": ("Croatia", 4.3),
    "CY": ("Cyprus", 4.1),
    "DE": ("Germany", 4.0),
    "NL": ("Netherlands", 4.0),
    "BG": ("Bulgaria", 3.5),
    "PL": ("Poland", 3.4),
    "SI": ("Slovenia", 3.4),
    "MT": ("Malta", 3.4),
    "CZ": ("Czechia", 3.2),
    "RU": ("Russia", 2.2),
}

STOPS = [
    {"at": 0, "color": "#F6E27A"},
    {"at": 0.28, "color": "#F0A23A"},
    {"at": 0.52, "color": "#E15A3C"},
    {"at": 0.75, "color": "#B2253A"},
    {"at": 1, "color": "#5A1228"},
]

CONTINENTS = ["Africa", "Asia", "Europe", "North America", "South America", "Oceania", "Antarctica"]
REGIONAL = {"ES": "es", "FR": "fr", "DE": "de", "IT": "it", "FI": "fi", "US": "us"}


def r1(value: float) -> float:
    return float(Decimal(str(value)).quantize(Decimal("0.1"), rounding=ROUND_HALF_UP))


def dist(p, a, b) -> float:
    (x, y), (x1, y1), (x2, y2) = p, a, b
    dx, dy = x2 - x1, y2 - y1
    if dx == 0 and dy == 0:
        return ((x - x1) ** 2 + (y - y1) ** 2) ** 0.5
    t = max(0.0, min(1.0, ((x - x1) * dx + (y - y1) * dy) / (dx * dx + dy * dy)))
    return ((x - (x1 + t * dx)) ** 2 + (y - (y1 + t * dy)) ** 2) ** 0.5


def simplify(points, epsilon: float):
    if len(points) < 3:
        return points
    end = len(points) - 1
    index, best = 0, 0.0
    for i in range(1, end):
        d = dist(points[i], points[0], points[end])
        if d > best:
            index, best = i, d
    if best > epsilon:
        left = simplify(points[: index + 1], epsilon)
        right = simplify(points[index:], epsilon)
        return left[:-1] + right
    return [points[0], points[end]]


def quantize_ring(ring, epsilon):
    opened = ring[:-1] if len(ring) > 1 and ring[0] == ring[-1] else list(ring)
    simplified = simplify(opened, epsilon)
    out = []
    for x, y in simplified:
        point = [round(float(x), 2), round(float(y), 2)]
        if not out or out[-1] != point:
            out.append(point)
    if len(out) >= 3 and out[0] != out[-1]:
        out.append(out[0])
    return out if len(out) >= 4 else None


def area(ring) -> float:
    total = 0.0
    for (x1, y1), (x2, y2) in zip(ring, ring[1:]):
        total += x1 * y2 - x2 * y1
    return abs(total) / 2


def clean_geometry(geometry, epsilon, min_area):
    gtype = geometry["type"]
    coords = geometry["coordinates"]
    polygons = []
    if gtype == "Polygon":
        polygons = [coords]
    elif gtype == "MultiPolygon":
        polygons = coords
    else:
        return None
    kept = []
    for polygon in polygons:
        rings = []
        for ring in polygon:
            cleaned = quantize_ring(ring, epsilon)
            if cleaned and (not rings or area(cleaned) >= min_area):
                rings.append(cleaned)
        if rings and area(rings[0]) >= min_area:
            kept.append(rings)
    if not kept:
        return None
    if len(kept) == 1:
        return {"type": "Polygon", "coordinates": kept[0]}
    return {"type": "MultiPolygon", "coordinates": kept}


def feature(fid, name, geometry, **props):
    properties = {"name": name}
    properties.update(props)
    return {"type": "Feature", "id": fid, "properties": properties, "geometry": geometry}


def iso_of(props) -> str | None:
    code = props.get("ISO_A2_EH") or props.get("ISO_A2")
    if code in (None, "-99"):
        code = props.get("ADM0_A3")
    if code in (None, "-99"):
        return None
    return code


def load_wb():
    rates, force = {}, {}
    for row in json.loads((SRC / "wb_u.json").read_text())[1]:
        if row.get("value") is None or row.get("date") != "2025":
            continue
        rates[row["country"]["id"]] = (float(row["value"]), row["country"]["value"])
    for row in json.loads((SRC / "wb_lf.json").read_text())[1]:
        if row.get("value") is None or row.get("date") != "2025":
            continue
        force[row["country"]["id"]] = float(row["value"])
    meta = json.loads((SRC / "wb_u.json").read_text())[0]
    return rates, force, meta.get("lastupdated")


def load_euro():
    values = {}
    updated = None
    for code in ("ES", "FR", "DE", "IT", "FI"):
        document = json.loads((SRC / f"euro_{code}.json").read_text())
        updated = document.get("updated")
        index = document["dimension"]["geo"]["category"]["index"]
        labels = document["dimension"]["geo"]["category"]["label"]
        raw = {int(k): v for k, v in document["value"].items()}
        for nuts_id, position in index.items():
            if position in raw:
                values[nuts_id] = (r1(raw[position]), labels.get(nuts_id, nuts_id))
    return values, updated


def load_bls():
    return json.loads((SRC / "bls.json").read_text())


def build_countries():
    document = json.loads((SRC / "countries.geojson").read_text())
    features, seen = [], set()
    for item in document["features"]:
        props = item["properties"]
        if props.get("CONTINENT") == "Seven seas (open ocean)":
            continue
        code = iso_of(props)
        if code is None or code in seen:
            continue
        geometry = clean_geometry(item["geometry"], 0.12, 0.04)
        if geometry is None:
            continue
        seen.add(code)
        name = props.get("NAME_LONG") or props.get("NAME") or props.get("ADMIN") or code
        features.append(feature(code, name, geometry, continent=props.get("CONTINENT") or "Unassigned"))
    if "MT" not in seen and (SRC / "countries50.geojson").exists():
        finer = json.loads((SRC / "countries50.geojson").read_text())
        for item in finer["features"]:
            props = item["properties"]
            if iso_of(props) != "MT":
                continue
            geometry = clean_geometry(item["geometry"], 0.02, 0.0001)
            if geometry is None:
                continue
            features.append(feature("MT", "Malta", geometry, continent="Europe"))
    features.sort(key=lambda item: item["id"])
    return {"type": "FeatureCollection", "features": features}


def build_regions(euro, bls):
    features = []
    nuts = json.loads((SRC / "nuts.geojson").read_text())
    for item in nuts["features"]:
        props = item["properties"]
        if props.get("CNTR_CODE") not in REGIONAL or props.get("CNTR_CODE") == "US":
            continue
        if props["CNTR_CODE"] not in ("ES", "FR", "DE", "IT", "FI"):
            continue
        geometry = clean_geometry(item["geometry"], 0.08, 0.008)
        if geometry is None:
            continue
        nuts_id = props["NUTS_ID"]
        name = euro.get(nuts_id, (None, props.get("NAME_LATN") or nuts_id))[1]
        features.append(feature(nuts_id, name, geometry, parent=props["CNTR_CODE"], kind="nuts2"))
    admin = json.loads((SRC / "admin1.geojson").read_text())
    for item in admin["features"]:
        props = item["properties"]
        if props.get("iso_a2") != "US":
            continue
        postal = props.get("postal")
        if postal not in bls:
            continue
        geometry = clean_geometry(item["geometry"], 0.12, 0.004)
        if geometry is None:
            continue
        features.append(feature(postal, props.get("name") or postal, geometry, parent="US", kind="state"))
    features.sort(key=lambda item: item["id"])
    return {"type": "FeatureCollection", "features": features}


def continent_of(feature_item, overrides):
    code = feature_item["id"]
    return overrides.get(code, feature_item["properties"]["continent"])


def weighted(members, rates, force):
    num = den = 0.0
    used, missing_weight, missing_rate = [], [], []
    for code, name in members:
        if code not in rates:
            missing_rate.append(code)
            continue
        if code not in force:
            missing_weight.append(code)
            continue
        num += rates[code][0] * force[code]
        den += force[code]
        used.append(code)
    value = r1(num / den) if den else None
    return value, used, missing_weight, missing_rate


def region_entry(code, name, value, child=None):
    entry = {"id": code, "name": name, "value": value}
    if child:
        entry["childLevelId"] = child
    return entry


def build_story(countries, regions, rates, force, wb_updated, euro, euro_updated, bls):
    by_continent = defaultdict(list)
    names = {}
    for item in countries["features"]:
        names[item["id"]] = item["properties"]["name"]
        by_continent[item["properties"]["continent"]].append(item["id"])

    def country_regions(continent):
        rows = []
        for code in sorted(by_continent[continent], key=lambda item: names[item]):
            if code not in rates:
                continue
            child = REGIONAL.get(code)
            # Regional drills for European countries hang off the Europe graphic,
            # except the United States, which hangs off North America.
            if continent != "North America" and code == "US":
                child = None
            if continent != "Europe" and code in ("ES", "FR", "DE", "IT", "FI"):
                child = None
            rows.append(region_entry(code, names[code], r1(rates[code][0]), child or "regional-unavailable"))
        return rows

    continent_regions = []
    continent_notes = []
    for continent in CONTINENTS:
        if continent == "Antarctica":
            continent_regions.append({"id": "Antarctica", "name": "Antarctica"})
            continue
        members = [(code, names[code]) for code in by_continent[continent]]
        value, used, missing_weight, missing_rate = weighted(members, rates, force)
        child = {
            "Africa": "africa",
            "Asia": "asia",
            "Europe": "europe",
            "North America": "north-america",
            "South America": "south-america",
            "Oceania": "oceania",
        }[continent]
        continent_regions.append(region_entry(continent, continent, value, child))
        continent_notes.append({
            "continent": continent,
            "value": value,
            "countriesWeighted": len(used),
            "noLabourForce": missing_weight,
            "noRate": missing_rate,
        })

    europe_regions = [
        region_entry(code, EUROPE_RATES[code][0], EUROPE_RATES[code][1], REGIONAL.get(code, "regional-unavailable"))
        for code in EUROPE_RATES
    ]

    def nuts_level(level_id, parent_name, parent_code, breadcrumb, title):
        rows = []
        for item in regions["features"]:
            props = item["properties"]
            if props.get("parent") != parent_code or props.get("kind") != "nuts2":
                continue
            nuts_id = item["id"]
            if nuts_id not in euro:
                rows.append({"id": nuts_id, "name": props["name"]})
                continue
            rows.append(region_entry(nuts_id, props["name"], euro[nuts_id][0]))
        insets = [item["id"] for item in regions["features"] if item["properties"].get("parent") == parent_code and item["id"].startswith("FRY")]
        if parent_code == "ES":
            insets = ["ES70"]
        return {
            "id": level_id,
            "title": title,
            "breadcrumb": breadcrumb,
            "parentLevelId": "europe",
            "geography": "regions",
            "parentGeographyId": parent_code,
            "projection": "Albers",
            "legendCaption": f"Unemployment rate, 2024",
            "footnote": f"Eurostat NUTS-2 unemployment rate, ages 15–74, all education levels, 2024 (lfst_r_lfu3rt). Updated {euro_updated}. Boundaries: Eurostat GISCO.",
            "sampleData": False,
            "insets": [{"id": item, "corner": "south"} for item in insets],
            "regions": rows,
        }

    state_rows = []
    for item in regions["features"]:
        if item["properties"].get("kind") != "state":
            continue
        postal = item["id"]
        state = bls[postal]
        state_rows.append(region_entry(postal, item["properties"]["name"], r1(state["value"])))
    period = next(iter(bls.values()))

    levels = [
        {
            "id": "world",
            "title": "Unemployment by continent",
            "breadcrumb": "World",
            "geography": "continents",
            "projection": "Robinson",
            "legendCaption": "Unemployment rate, 2025",
            "footnote": "Labour-force-weighted average of ILO modelled national rates (World Bank SL.UEM.TOTL.ZS, 2025; labour force SL.TLF.TOTL.IN). World Bank updated " + str(wb_updated) + ". Countries without a labour-force total are on the next map and out of the average. Boundaries: Natural Earth, public domain.",
            "sampleData": False,
            "regions": continent_regions,
        },
        {
            "id": "europe",
            "title": "UNEMPLOYMENT IN EUROPE",
            "breadcrumb": "Europe",
            "parentLevelId": "world",
            "geography": "countries",
            "continent": "Europe",
            "projection": "Albers",
            "extent": {"west": -15, "south": 34.5, "east": 50, "north": 72},
            "clip": True,
            "legendMin": 2.2,
            "legendMax": 10.3,
            "legendCaption": "Unemployment rate (Aug. 2026)",
            "footnote": "Switzerland and UK data from June 2026. Sources: Eurostat, ONS, Rosstat via Moscow Times.",
            "sampleData": False,
            "defaultChildLevelId": "regional-unavailable",
            "insets": [{"id": "IS", "corner": "west"}, {"id": "CY", "corner": "southeast"}],
            "callouts": [
                {"id": "finland", "regionId": "FI", "placement": "east", "text": "Weak hiring is keeping unemployment high in Finland."},
                {"id": "germany", "regionId": "DE", "placement": "west", "text": "Germany's automakers are cutting jobs amid U.S. tariffs and growing Chinese competition."},
            ],
            "regions": europe_regions,
        },
    ]
    for continent, level_id, title in (
        ("Africa", "africa", "Unemployment in Africa"),
        ("Asia", "asia", "Unemployment in Asia"),
        ("North America", "north-america", "Unemployment in North America"),
        ("South America", "south-america", "Unemployment in South America"),
        ("Oceania", "oceania", "Unemployment in Oceania"),
    ):
        levels.append({
            "id": level_id,
            "title": title,
            "breadcrumb": continent,
            "parentLevelId": "world",
            "geography": "countries",
            "continent": continent,
            "projection": "Albers" if continent != "Asia" else "Mercator",
            "legendCaption": "Unemployment rate, 2025",
            "footnote": "ILO modelled estimates via World Bank SL.UEM.TOTL.ZS, 2025 (updated " + str(wb_updated) + "). Shown to 0.1 percentage point. Boundaries: Natural Earth, public domain.",
            "sampleData": False,
            "defaultChildLevelId": "regional-unavailable",
            "regions": country_regions(continent),
        })
    levels.extend([
        nuts_level("es", "Spain", "ES", "Spain", "Unemployment in Spain"),
        nuts_level("fr", "France", "FR", "France", "Unemployment in France"),
        nuts_level("de", "Germany", "DE", "Germany", "Unemployment in Germany"),
        nuts_level("it", "Italy", "IT", "Italy", "Unemployment in Italy"),
        nuts_level("fi", "Finland", "FI", "Finland", "Unemployment in Finland"),
        {
            "id": "us",
            "title": "Unemployment in the United States",
            "breadcrumb": "United States",
            "parentLevelId": "north-america",
            "geography": "regions",
            "parentGeographyId": "US",
            "projection": "Albers",
            "legendCaption": "Unemployment rate (Sep. 2026)",
            "footnote": f"U.S. Bureau of Labor Statistics, Local Area Unemployment Statistics, seasonally adjusted statewide rates, {period['period']} {period['year']} (preliminary). The national ILO 2025 rate on the previous map is a different series.",
            "sampleData": False,
            "insets": [{"id": "AK", "corner": "west"}, {"id": "HI", "corner": "south"}],
            "regions": state_rows,
        },
        {
            "id": "regional-unavailable",
            "title": "Regional data not available",
            "breadcrumb": "Regions",
            "geography": "country",
            "projection": "Albers",
            "unavailableMessage": "Regional data not available",
            "footnote": "This story has provincial figures for Spain, France, Germany, Italy, Finland (Eurostat NUTS-2, 2024) and the United States (BLS). Other countries stop at the national rate.",
            "sampleData": False,
            "regions": [],
        },
    ])
    story = {
        "id": "unemployment",
        "title": "Unemployment",
        "valueFormat": "0.0",
        "valueSuffix": "%",
        "noDataColor": "#C5C6C4",
        "stroke": "#ffffff",
        "background": "#F7F5F2",
        "stops": STOPS,
        "rootLevelId": "world",
        "sources": [
            {"levelId": "world", "publisher": "World Bank", "dataset": "SL.UEM.TOTL.ZS and SL.TLF.TOTL.IN", "period": "2025", "retrieved": "2026-10-21", "note": "ILO modelled estimates. Continent values are labour-force-weighted averages of the countries drawn. World Bank lastupdated " + str(wb_updated) + "."},
            {"levelId": "europe", "publisher": "Eurostat, ONS, Rosstat via Moscow Times", "dataset": "Visual Capitalist reference graphic", "period": "August 2026 (Switzerland and UK: June 2026)", "retrieved": "2026-10-21", "note": "Exact rates from the reference graphic, not the World Bank 2025 series."},
            {"levelId": "es", "publisher": "Eurostat", "dataset": "lfst_r_lfu3rt", "period": "2024", "retrieved": "2026-10-21", "note": "NUTS-2, sex total, ages 15-74, all ISCED levels, unit PC. Updated " + str(euro_updated) + "."},
            {"levelId": "us", "publisher": "U.S. Bureau of Labor Statistics", "dataset": "LAUS LASST statewide unemployment rate, seasonally adjusted", "period": f"{period['period']} {period['year']}", "retrieved": "2026-10-21", "note": "Preliminary. Public API v2."},
        ],
        "levels": levels,
    }
    return story, continent_notes


def main():
    rates, force, wb_updated = load_wb()
    euro, euro_updated = load_euro()
    bls = load_bls()
    countries = build_countries()
    regions = build_regions(euro, bls)
    story, notes = build_story(countries, regions, rates, force, wb_updated, euro, euro_updated, bls)
    OUT_GEO.mkdir(parents=True, exist_ok=True)
    OUT_STORY.mkdir(parents=True, exist_ok=True)
    (OUT_GEO / "countries.geojson").write_text(json.dumps(countries, separators=(",", ":")))
    (OUT_GEO / "regions.geojson").write_text(json.dumps(regions, separators=(",", ":")))
    (OUT_STORY / "unemployment.json").write_text(json.dumps(story, indent=2) + "\n")
    print("countries", len(countries["features"]), (OUT_GEO / "countries.geojson").stat().st_size)
    print("regions", len(regions["features"]), (OUT_GEO / "regions.geojson").stat().st_size)
    print("story", (OUT_STORY / "unemployment.json").stat().st_size)
    print("euro regions", len(euro), "missing nuts", [item["id"] for item in regions["features"] if item["properties"].get("kind") == "nuts2" and item["id"] not in euro])
    for note in notes:
        print(note["continent"], note["value"], "weighted", note["countriesWeighted"], "no rate", len(note["noRate"]), "no force", note["noLabourForce"])
    us = [item for item in story["levels"] if item["id"] == "us"][0]
    values = [item["value"] for item in us["regions"]]
    print("us states", len(values), "min", min(values), "max", max(values))


if __name__ == "__main__":
    main()
