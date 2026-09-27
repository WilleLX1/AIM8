# AIM8

An 8-ball aim assist for a pool game on your iPhone, shown on the PC. It
mirrors the phone the way iBridge does, reads each frame, finds the table and
every ball, works out which group each ball belongs to, and draws the best
shot for your group on top of the phone image.

```
src/AIM8.Core     vision, shot solver, overlay JSON, pymobiledevice3 services (ported from iBridge)
src/AIM8.App      WPF window (.NET 10, WebView2) with the live mirror and the overlay
tests/AIM8.Core.Tests
```

## Run it

```bash
dotnet run --project src/AIM8.App -c Release
```

Same prerequisites as iBridge: Windows 10/11 with WebView2, the .NET 10 SDK,
Python with `pymobiledevice3` (`pip install -U pymobiledevice3`), and Apple's
USB driver. On the phone: pair it once and tap Trust, turn on Developer Mode,
and keep it unlocked.

- **Live mirror** needs iOS 27 or later (Apple's gate; see iBridge's SETUP.md).
  AIM8 starts it by itself when the phone appears. Mouse on the phone image is
  touch on the phone, so you can aim from the PC as well.
- **Older iOS**: if the phone refuses to mirror, the live screen view reports
  the problem rather than showing a delayed screenshot as if it were live.
  **Screenshots** remains an explicit, slower mode for analysis.
- If the stream says the iPhone camera or microphone is in use, close the app
  using that sensor on the phone and press **Live** or **Stream** to try live
  mirroring again.
- **Run as administrator** for a shared kernel tunnel. Without it every
  developer command builds its own userspace tunnel, which is slower and seems
  to make the phone stream at a lower resolution (384 × 832 on an iPhone 11
  here, instead of full size).
- **No phone?** Use **Demo table** (a rendered random layout) or **Open
  screenshot…**.

## Using it

1. Pick your group: **Whole balls** (solids 1–7) or **Half balls** (stripes
   9–15). Use **Open table** after the break while groups are still undecided.
   Once your group is cleared, it goes for the 8.
2. Wait until the balls stop. The plan is drawn once the table has been still
   for two frames:
   - white line: the cue ball's path; the dashed circle is where the cue ball
     must be at contact (the ghost ball)
   - line in your group's colour: the object ball into the highlighted pocket
   - dotted line: where the cue ball goes after contact without spin (red
     means it risks a scratch)
   - faint lines: the runner-up shots
3. The right panel shows the same plan in words, with a rough chance and the
   cut angle.

The live screen view opens automatically once the first stream frame arrives.
The real-time iPhone image fills the window and AIM8 draws only the best shot
directly over it. Use **Settings** in the image (or Esc) to see the detailed
controls and console; use **Live screen view** there to return to the stream.
The ↶ and ↷ buttons beside **Live screen view**, or beside **Settings** in
fullscreen, rotate the mirrored phone and its touch/analysis coordinates
together.
Saved screenshots and demo tables stay in the settings view and never silently
replace the live image. Repeated screenshots are slower and require touch
input on the iPhone.

Every ball gets a ring: solid ring for whole balls, dashed for half balls,
white for the cue ball, and a white rim for the 8. Your targets get a second
ring in your group's colour.

### Shots it considers

Direct pots, banks (object ball off one cushion), kicks (cue ball off one
cushion), two-ball combinations of your own balls, the break (a full rack,
even when it shows as one blob at phone resolution), and a safety when nothing
pots. A missing cue-ball detection does not assume ball in hand or draw a shot.
Shots are ranked
by how much aiming error they tolerate: the pocket's width seen from the
object ball, narrowed by the cut angle and by the cue-ball distance.

## When detection is off

- **Calibrate**: drag from one inner cushion corner to the opposite corner on
  the phone image. **Auto** goes back to detection. The rectangle is saved.
- **Cloth tolerance**: raise it if patches of cloth show up as balls, lower it
  if balls are missed.
- **Stripe split / Ball size**: take them off *auto* if a game draws balls
  unusually.
- **Save frame** writes exactly what the analysis saw to
  `Pictures\AIM8`. To run the pipeline on one offline and see what it made of
  each ball:

  ```bash
  python -c "import cv2,sys; im=cv2.cvtColor(cv2.imread(sys.argv[1]),cv2.COLOR_BGR2RGBA); open(sys.argv[2],'wb').write(im.shape[1].to_bytes(4,'little')+im.shape[0].to_bytes(4,'little')+im.tobytes())" frame.png frame.rgba
  ```

  ```bash
  AIM8_FRAME=frame.rgba dotnet test tests/AIM8.Core.Tests --filter RealFrameProbe --logger "console;verbosity=detailed"
  ```

## How it works

**Frames.** The pymobiledevice3 viewer decodes the phone's HEVC stream into a
`<canvas>`. An injected script (`src/AIM8.App/Web/overlay.js`) copies that
canvas into a WebView2 shared buffer and tells the host a frame is ready. The
host analyses it on a worker thread and posts the result back as JSON, which
the same script draws on a canvas laid over the phone image with pointer
events turned off. One frame is in flight at a time, about 8 per second.
Manually selected screenshots and the demo use `offline.html`, which
puts the image on a canvas of the same id, so the overlay does not care where
a frame came from. iBridge's WebCodecs size fix for Edge is injected too.

**Table.** The cloth is the dominant saturated hue in the middle of the screen.
The largest region of it (pieces split by a stick lying across the table are
merged back) gives the table. Its edges are then refined at full resolution by
walking out from the bed to the first solid line of non-cloth (the dark
cushion nose many games draw), plus a check for cushions drawn in the cloth's
own colour. The pockets are the corners and the middle of the long sides.

**Balls.** Everything that is not cloth is "object". The Euclidean distance
transform of that mask peaks at each ball's centre with a height of one
radius, and keeps separate peaks for touching balls because their outlines
pinch in where they meet. Peaks are then:

- checked for a pinch between neighbours (the cue stick is a ridge without one)
- ray-cast for a round outline
- circle-fitted for sub-pixel centres

The radius is voted on by round peaks, weighted towards the size seen in
earlier frames. Two clean-ups come first: small cloth-coloured specks inside
a ball are filled in, and a light closing handles a ball the colour of the
cloth (green 6 on green cloth). The cue stick is found as a straight crest in
the distance field and masked out when reading colours.

**Kinds.** The cue ball is white right out to its rim, and the 8 is mostly
black. A stripe carries its white in two caps at the edge; a solid only has
its number disc, near the middle. The hue gives the number, and the two balls
of one colour are forced into different groups. A tracker averages positions,
votes on each ball's kind across frames, keeps balls hidden under the aim ring
or the stick, and says when the table has stopped moving.

## Tests

```bash
dotnet test
```

55 tests, no phone needed. They run against rendered tables (green and blue
cloth, portrait and landscape, a full rack, the stick lying over balls):
table edges, every ball's position, kind and number, the solver's shot types
and rules, and the whole engine including the overlay JSON. The robustness
test runs 40 random layouts per case. Currently: 100% of balls right on blue
cloth, 99.8% on green, and 97% with the stick lying over a ball in a single
frame (live, the tracker's vote covers that).

If the test host says it found only .NET 9, point `DOTNET_ROOT` at
`C:\Program Files\dotnet`, as in iBridge.

## Known limits

- Tuned against 8 Ball Pool and rendered tables. Another game's art may need
  the Detection sliders or a calibration.
- A ball the same colour as the cloth and just as saturated cannot be told
  from the cloth.
- At the phone's reduced stream resolution a fresh rack is one blob. AIM8
  plans the break from it but cannot name the balls until they spread.
- A ball under the game's own aim ring can be missed in a single frame. Once
  it has been seen, the tracker keeps it while it is covered.
- Settings live in `%APPDATA%\AIM8\config.json`. Pages and screenshots go to
  `%USERPROFILE%\.aim8\web`, outside AppData, because a Microsoft Store
  Python has its AppData writes redirected.
