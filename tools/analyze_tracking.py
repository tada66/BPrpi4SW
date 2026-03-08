#!/usr/bin/env python3
"""Analyze celestial tracking positions from log data to find drift/jitter."""
import re
import sys
import numpy as np

# Parse tracking lines from log
pattern = re.compile(
    r'\[INFO\] (\d{4}-\d{2}-\d{2} (\d{2}:\d{2}:\d{2}))'
    r'.*Positions: X=(-?\d+), Y=(-?\d+), Z=(-?\d+) arcseconds'
    r'.*Celestial Tracking: TRACKING'
)

lines = open(sys.argv[1], encoding='utf-8').readlines()
data = []

# Optional time filter: --from HH:MM:SS --to HH:MM:SS
time_from = 0
time_to = 24*3600
for i, arg in enumerate(sys.argv):
    if arg == '--from' and i+1 < len(sys.argv):
        h, mn, s = map(int, sys.argv[i+1].split(':'))
        time_from = h*3600 + mn*60 + s
    if arg == '--to' and i+1 < len(sys.argv):
        h, mn, s = map(int, sys.argv[i+1].split(':'))
        time_to = h*3600 + mn*60 + s

for line in lines:
    m = pattern.search(line)
    if not m:
        continue
    time_str = m.group(2)
    h, mn, s = map(int, time_str.split(':'))
    t = h * 3600 + mn * 60 + s
    if t < time_from or t > time_to:
        continue
    x = int(m.group(3))
    y = int(m.group(4))
    z = int(m.group(5))
    data.append((t, x, y, z))

if not data:
    print("No tracking data found!")
    sys.exit(1)

# Find session breaks (>30s gap)
sessions = []
current = [data[0]]
for i in range(1, len(data)):
    if data[i][0] - data[i-1][0] > 30:
        sessions.append(current)
        current = [data[i]]
    else:
        current.append(data[i])
sessions.append(current)

print(f"Found {len(sessions)} tracking session(s)\n")

for si, session in enumerate(sessions):
    arr = np.array(session, dtype=np.float64)
    t = arr[:, 0] - arr[0, 0]  # time from start
    X = arr[:, 1]
    Y = arr[:, 2]
    Z = arr[:, 3]
    
    duration = t[-1] - t[0]
    n = len(t)
    
    print(f"=== Session {si+1}: {n} points, {duration:.0f}s ===")
    print(f"  Time: {session[0][0]//3600:02d}:{(session[0][0]%3600)//60:02d}:{session[0][0]%60:02d} to {session[-1][0]//3600:02d}:{(session[-1][0]%3600)//60:02d}:{session[-1][0]%60:02d}")
    print(f"  X: {X[0]:.0f} -> {X[-1]:.0f} (delta={X[-1]-X[0]:.0f} arcsec)")
    print(f"  Y: {Y[0]:.0f} -> {Y[-1]:.0f} (delta={Y[-1]-Y[0]:.0f} arcsec)")
    print(f"  Z: {Z[0]:.0f} -> {Z[-1]:.0f} (delta={Z[-1]-Z[0]:.0f} arcsec)")
    
    if duration < 10 or n < 5:
        print("  (too short for analysis)\n")
        continue
    
    # Linear fit for each axis
    for axis_name, vals in [('X', X), ('Y', Y), ('Z', Z)]:
        coeffs = np.polyfit(t, vals, 1)
        rate = coeffs[0]
        fitted = np.polyval(coeffs, t)
        residuals = vals - fitted
        rms = np.sqrt(np.mean(residuals**2))
        max_resid = np.max(np.abs(residuals))
        
        print(f"\n  {axis_name} axis:")
        print(f"    Rate: {rate:.4f} arcsec/sec")
        print(f"    Linear fit RMS residual: {rms:.2f} arcsec ({rms/44.0:.2f} px)")
        print(f"    Max residual: {max_resid:.2f} arcsec ({max_resid/44.0:.2f} px)")
        
        # Check for quadratic drift
        if n >= 10:
            coeffs2 = np.polyfit(t, vals, 2)
            fitted2 = np.polyval(coeffs2, t)
            residuals2 = vals - fitted2
            rms2 = np.sqrt(np.mean(residuals2**2))
            accel = coeffs2[0] * 2  # d²/dt² 
            drift_120 = 0.5 * accel * 120**2  # drift in 120 seconds
            print(f"    Quadratic fit: accel={accel:.6f} arcsec/s², drift in 2min={drift_120:.1f} arcsec ({drift_120/44.0:.1f} px)")
            print(f"    Quadratic RMS residual: {rms2:.2f} arcsec ({rms2/44.0:.2f} px)")
    
    # Compute combined XZ drift (total on-sky position error)
    print(f"\n  Combined analysis:")
    cx = np.polyfit(t, X, 2)
    cz = np.polyfit(t, Z, 2)
    fx = np.polyval(cx, t)
    fz = np.polyval(cz, t)
    rx = X - np.polyval(np.polyfit(t, X, 1), t)
    rz = Z - np.polyval(np.polyfit(t, Z, 1), t)
    combined_resid = np.sqrt(rx**2 + rz**2)
    print(f"    Combined linear residual (RSS): max={np.max(combined_resid):.2f} arcsec ({np.max(combined_resid)/44.0:.2f} px)")
    
    # Look at step quantization - are positions always multiples of step size?
    x_steps = np.diff(X)
    z_steps = np.diff(Z)
    unique_x = np.unique(x_steps)
    unique_z = np.unique(z_steps)
    print(f"\n  Step analysis (2-sec increments):")
    print(f"    X increments: {unique_x}")
    print(f"    Z increments: {unique_z}")
    
    print()
