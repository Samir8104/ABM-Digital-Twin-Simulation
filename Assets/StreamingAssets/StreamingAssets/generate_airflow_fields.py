"""
generate_airflow_fields.py
--------------------------
Generates synthetic 3D velocity field CSVs for testing the Unity particle system.
Produces three scenarios: uniform flow, vortex, and a simplified HVAC-like field.

Coordinate system: Unity convention (y = vertical/up)
Units: meters, meters/second

Usage:
    python generate_airflow_fields.py

Outputs:
    field_uniform.csv
    field_vortex.csv
    field_hvac.csv
"""

import numpy as np
import pandas as pd
import os

# ---------------------------------------------------------------------------
# Room dimensions (adjust to match your Unity scene)
# ---------------------------------------------------------------------------
X_MIN, X_MAX = -50.0,  35.0   # ~85m wide, padded
Y_MIN, Y_MAX =  -5.0,  25.0   # covers ground floor, two floors, ceiling
Z_MIN, Z_MAX = -55.0,  35.0   # ~90m deep, padded

RESOLUTION = 1.0  # coarser than before — keeps file size reasonable

# ---------------------------------------------------------------------------
# Build the grid
# ---------------------------------------------------------------------------
xs = np.arange(X_MIN, X_MAX + RESOLUTION, RESOLUTION)
ys = np.arange(Y_MIN, Y_MAX + RESOLUTION, RESOLUTION)
zs = np.arange(Z_MIN, Z_MAX + RESOLUTION, RESOLUTION)

XX, YY, ZZ = np.meshgrid(xs, ys, zs, indexing='ij')
x_flat = XX.ravel()
y_flat = YY.ravel()
z_flat = ZZ.ravel()

print(f"Grid: {len(xs)} x {len(ys)} x {len(zs)} = {len(x_flat):,} cells")
print(f"Room: {X_MAX}m wide, {Y_MAX}m tall, {Z_MAX}m deep")
print()

# ---------------------------------------------------------------------------
# Helper: save to CSV
# ---------------------------------------------------------------------------
def save_field(filename, u, v, w):
    df = pd.DataFrame({
        'x': x_flat, 'y': y_flat, 'z': z_flat,
        'u': u,      'v': v,      'w': w
    })
    df = df.round(6)
    df.to_csv(filename, index=False)
    size_kb = os.path.getsize(filename) / 1024
    print(f"  Saved: {filename}  ({len(df):,} rows, {size_kb:.1f} KB)")


# ---------------------------------------------------------------------------
# Scenario 1: Uniform flow
# Particles drift in +x direction at 0.2 m/s.
# Use first to confirm the field reader and particle advection work at all.
# ---------------------------------------------------------------------------
print("Generating field_uniform.csv ...")
u1 = np.full_like(x_flat, 0.2)
v1 = np.zeros_like(x_flat)
w1 = np.zeros_like(x_flat)
save_field("field_uniform.csv", u1, v1, w1)


# ---------------------------------------------------------------------------
# Scenario 2: Vortex (horizontal rotation)
# Particles orbit the center of the room in the XZ plane.
# Use this to confirm direction changes are handled correctly.
# ---------------------------------------------------------------------------
print("Generating field_vortex.csv ...")
cx = (X_MAX + X_MIN) / 2  # center x
cz = (Z_MAX + Z_MIN) / 2  # center z

dx = x_flat - cx
dz = z_flat - cz
r  = np.sqrt(dx**2 + dz**2) + 1e-6  # avoid div-by-zero

speed = 0.3  # m/s tangential speed
u2 =  speed * (-dz / r)   # tangent in XZ plane
v2 =  np.zeros_like(x_flat)
w2 =  speed * ( dx / r)
save_field("field_vortex.csv", u2, v2, w2)


# ---------------------------------------------------------------------------
# Scenario 3: Simplified HVAC-like flow
# Supply vent at ceiling (high x, high y) pushes air downward and across.
# Return vent at floor (low x, low y) pulls it back.
# This is physically crude but gives a realistic plume shape for testing.
# ---------------------------------------------------------------------------
print("Generating field_hvac.csv ...")

# Supply vent: top-right corner of the room
vent_x, vent_y, vent_z = X_MAX, Y_MAX, Z_MAX / 2
# Return vent: bottom-left corner
ret_x,  ret_y,  ret_z  = X_MIN, Y_MIN, Z_MAX / 2

# Vector from each cell toward the return vent (simplified "pull")
to_ret_x = ret_x - x_flat
to_ret_y = ret_y - y_flat
to_ret_z = ret_z - z_flat
dist_ret  = np.sqrt(to_ret_x**2 + to_ret_y**2 + to_ret_z**2) + 1e-6

# Vector from supply vent toward each cell (simplified "push")
from_sup_x = x_flat - vent_x
from_sup_y = y_flat - vent_y
from_sup_z = z_flat - vent_z
dist_sup   = np.sqrt(from_sup_x**2 + from_sup_y**2 + from_sup_z**2) + 1e-6

# Blend: near supply → dominated by push; near return → dominated by pull
weight_sup = 1.0 / (dist_sup + 0.5)
weight_ret = 1.0 / (dist_ret + 0.5)
total_w    = weight_sup + weight_ret

u3 = (weight_sup * (from_sup_x / dist_sup) + weight_ret * (to_ret_x / dist_ret)) / total_w
v3 = (weight_sup * (from_sup_y / dist_sup) + weight_ret * (to_ret_y / dist_ret)) / total_w
w3 = (weight_sup * (from_sup_z / dist_sup) + weight_ret * (to_ret_z / dist_ret)) / total_w

# Scale to a reasonable indoor air speed (~0.1–0.3 m/s)
scale = 0.2
u3 *= scale
v3 *= scale
w3 *= scale

save_field("field_hvac.csv", u3, v3, w3)


# ---------------------------------------------------------------------------
# Print summary for Unity setup
# ---------------------------------------------------------------------------
print()
print("=" * 55)
print("Unity setup values (copy into your C# field reader):")
print("=" * 55)
print(f"  xMin = {X_MIN}f,  xMax = {X_MAX}f")
print(f"  yMin = {Y_MIN}f,  yMax = {Y_MAX}f")
print(f"  zMin = {Z_MIN}f,  zMax = {Z_MAX}f")
print(f"  dx = dy = dz = {RESOLUTION}f")
print(f"  Nx = {len(xs)}, Ny = {len(ys)}, Nz = {len(zs)}")
print()
print("Coordinate convention used:")
print("  x = horizontal (width)")
print("  y = vertical   (height, UP in Unity)")
print("  z = depth")
print()
print("Start with field_uniform.csv — if particles move in +x, the")
print("pipeline works. Then swap in field_vortex.csv or field_hvac.csv.")
