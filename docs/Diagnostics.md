# Diagnostics

Diagnostics groups actionable problems, the core audio path, playback and listening, configuration, and optional features. Its health summary and repair rows come from the same `SystemReadinessSnapshot` used by Dashboard and Setup. Export writes the displayed `DiagnosticSnapshot`, including the same check results and technical observations, rather than running a second health calculation.

The advanced section reports Remote API and Windows B1 meter evidence, provider paths, XInput controllers, Music Library references, Clip Guard / Output Health, and station configuration. A Remote API meter shows a measured point; game or voice-app reception still requires owner confirmation. Optional providers and controllers never block core Ready. A mixer/Windows B1 telemetry conflict does block Ready and disables automatic Clip Guard protection.

Repair buttons initiate explicit actions such as reconnecting the Audio Bridge or refreshing devices. Opening Diagnostics and exporting a report are read-only. The Voicemeeter limiter check reads the selected music strip without changing it; enabling the opt-in limiter is a separate Audio & Routing action. Reports exclude credentials, browser data, cookies, audio samples, and unrelated files.
