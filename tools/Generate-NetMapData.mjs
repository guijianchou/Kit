// Generate compact offline map data from the pinned Natural Earth v5.1.2 GeoJSON files.
// Usage: node tools/Generate-NetMapData.mjs <source-directory>
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const source = process.argv[2];
if (!source) throw new Error('Provide the directory containing Natural Earth v5.1.2 GeoJSON files.');
const read = name => JSON.parse(fs.readFileSync(path.join(source, `${name}.geojson`), 'utf8')).features;
const round = point => point.map(value => Math.round(value * 1000) / 1000);
const lines = geometry => ['Polygon', 'MultiLineString'].includes(geometry.type) ? geometry.coordinates
    : geometry.type === 'MultiPolygon' ? geometry.coordinates.flat() : [geometry.coordinates];
// Remove subpixel vertices without reducing the detailed map to the overview geometry.
// A fixed angular tolerance preserves reproducibility and avoids runtime simplification.
function simplify(points, tolerance = 0.015) {
    if (points.length <= 4) return points.map(round);
    const keep = new Set([0, points.length - 1]);
    const pending = [[0, points.length - 1]];
    while (pending.length) {
        const [first, last] = pending.pop();
        const a = points[first], b = points[last];
        const dx = b[0] - a[0], dy = b[1] - a[1], length = dx * dx + dy * dy;
        let distance = tolerance * tolerance, selected = -1;
        for (let i = first + 1; i < last; i++) {
            const p = points[i];
            const t = length ? Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / length)) : 0;
            const d = (p[0] - a[0] - t * dx) ** 2 + (p[1] - a[1] - t * dy) ** 2;
            if (d > distance) { distance = d; selected = i; }
        }
        if (selected >= 0) { keep.add(selected); pending.push([first, selected], [selected, last]); }
    }
    const result = [...keep].sort((a, b) => a - b).map(i => round(points[i]));
    // Retain small islands and closed polygon rings even below the tolerance.
    return result.length < 4 && points[0][0] === points.at(-1)[0] && points[0][1] === points.at(-1)[1]
        ? points.map(round) : result;
}

const countries = read('ne_50m_admin_0_countries').map(({ properties: p, geometry }) => ({
    code: p.ISO_A2 === '-99' ? p.ISO_A2_EH : p.ISO_A2,
    name: p.NAME_EN || p.NAME, nameZh: p.NAME_ZH || '',
    label: round([p.LABEL_X, p.LABEL_Y]), rank: p.LABELRANK,
    rings: lines(geometry).map(ring => simplify(ring)),
}));
const coastlines = read('ne_50m_coastline').flatMap(({ geometry }) => lines(geometry).map(line => simplify(line)));
const cities = read('ne_50m_populated_places').filter(({ properties: p }) => p.SCALERANK <= 4 || p.ADM0CAP === 1)
    .map(({ properties: p, geometry }) => ({
        name: p.NAME_EN || p.NAME, nameZh: p.NAME_ZH || '', code: p.ISO_A2,
        label: round(geometry.coordinates), rank: p.SCALERANK, capital: p.ADM0CAP === 1,
    })).sort((a, b) => a.rank - b.rank || a.name.localeCompare(b.name, 'en'));
const destination = fileURLToPath(new URL('../src/modules/NetMap/NetMapLib/Data/world-detail.json', import.meta.url));
fs.writeFileSync(destination, JSON.stringify({ countries, coastlines, cities }) + '\n');
console.log(JSON.stringify({ destination, countries: countries.length, coastlines: coastlines.length, cities: cities.length,
    countryVertices: countries.reduce((sum, country) => sum + country.rings.reduce((count, ring) => count + ring.length, 0), 0),
    bytes: fs.statSync(destination).size }));
