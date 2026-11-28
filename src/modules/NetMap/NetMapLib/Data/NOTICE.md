# Map source

Natural Earth v5.1.2, 1:110m admin-0 countries, public domain.
https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_110m_admin_0_countries.geojson

https://www.naturalearthdata.com/about/terms-of-use/

Reduced to outlines, ISO A2 identifiers, published label coordinates and Chinese country names (NAME_ZH). Coordinates rounded to 0.001 degrees. Displayed country points are regional illustrations, not IP or device locations. The map is embedded; there is no runtime map updater.

`world-detail.json` additionally contains Natural Earth v5.1.2 1:50m country polygons,
coastlines and populated places, also public domain:

- https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_50m_admin_0_countries.geojson
- https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_50m_coastline.geojson
- https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_50m_populated_places.geojson

Generate with `node tools/Generate-NetMapData.mjs <directory-containing-these-files>`.
The generator simplifies lines with a 0.015-degree tolerance and rounds coordinates
to 0.001 degrees, retaining small closed island rings. Country/city names and label
rankings come from the source; cities include capitals and scalerank 0–4 places.
Geographic labels describe the basemap and do not imply precise router locations.
Boundaries follow Natural Earth's source dataset and are illustrative.
