# Tript build & packaging Makefile.
#
# Targets:
#   all        build the current OS (Debug) — the dev loop
#   dev        build + run the app (Debug, --fake-recorder by default for a no-hardware dev session)
#   release    build the current OS in Release
#   linux      publish a framework-dependent Linux build into dist/<config>
#   windows    publish a Windows build with Tript.exe at dist/<config>-win and its runtime under App/
#   shell      build the frontend + publish the desktop shell (Tript.Shell, Photino window)
#   publish-shell-win  publish the Windows desktop shell (Tript.Shell, Photino window) into App/
#   launcher-windows   build the native Windows Tript.exe launcher (requires mingw-w64)
#   obs-fetch  download the pinned OBS Studio Windows portable zip into third_party/
#   test       run the .NET test suite + the frontend Vitest suite
#   run        run the assembled binary — prefers the desktop shell, falls back to the headless host
#   clean      remove build outputs
#
# Flags (defaults):
#   CONFIG=Debug|Release          build configuration (default Debug)
#   RID=linux-x64|win-x64         runtime identifier (default linux-x64)
#   OBS_VERSION=32.2.2            pinned OBS Studio Windows version
#   SELF_CONTAINED=false          self-contained .NET publish (Windows bundle defaults true)
#   DIST_DIR=dist                 output directory for assembled builds
#   FAKE_RECORDER=true|false      pass --fake-recorder to `run` (default true for dev)
#   TRAINING=true|false           include the model-training feature set (default false)
#   OBS_URL=<url>                 override the OBS zip download URL
#   WIN_LAUNCHER_CC=<compiler>    mingw-w64 C compiler (default x86_64-w64-mingw32-gcc)
#   WIN_LAUNCHER_WINDRES=<tool>   mingw-w64 resource compiler (default x86_64-w64-mingw32-windres)
#
# The Linux build is framework-dependent and expects OBS as a system dependency (the app
# discovers it at runtime). The Windows build bundles a pinned OBS Studio portable zip.
# Linux places the frontend beside the host as ./dist. Windows packages place the host and frontend
# under App/ so the package root contains only the user-facing launcher.
#
# The desktop shell (shell/publish-shell) publishes src/Tript.Shell next to the app host. The shell
# reuses the app host's construction seam and points a native Photino window at the same UI. On
# Linux it needs the webkit2gtk-4.1 system package (NOT webkitgtk-6.0) — see README. `run` prefers
# the shell binary when it has been built and falls back to the headless host (which prints the UI
# URL at http://localhost:8892/).

CONFIG ?= Debug
RID ?= linux-x64
OBS_VERSION ?= 32.2.2
SELF_CONTAINED ?= false
DIST_DIR ?= dist
FAKE_RECORDER ?= true
TRAINING ?= false
OBS_URL ?= https://github.com/obsproject/obs-studio/releases/download/$(OBS_VERSION)/OBS-Studio-$(OBS_VERSION)-Windows-x64.zip

# ---- derived paths ----
WEB_SRC := src/Tript.Web
APP_CS := src/Tript.App
PUBLISH_DIR := $(DIST_DIR)/$(CONFIG)
WIN_PUBLISH_DIR := $(DIST_DIR)/$(CONFIG)-win
WIN_APP_DIR := $(WIN_PUBLISH_DIR)/App
SHELL_BIN := $(PUBLISH_DIR)/Tript.Shell
OBS_ARCHIVE := third_party/obs-studio-$(OBS_VERSION).zip
OBS_DIR := third_party/obs-studio-$(OBS_VERSION)
OBS_EXTRACTED := third_party/obs-studio-$(OBS_VERSION)-x64
WIN_LAUNCHER_CC ?= x86_64-w64-mingw32-gcc
WIN_LAUNCHER_WINDRES ?= x86_64-w64-mingw32-windres

.PHONY: all dev release linux windows obs-fetch restore-windows test test-dotnet test-web test-integration test-all
.PHONY: run clean shell publish-shell
.PHONY: web frontend publish publish-linux publish-windows publish-shell-win assemble-windows launcher-windows

all: linux

# ---- web (frontend) ----
web frontend:
	cd $(WEB_SRC) && npm ci && VITE_TRIPT_TRAINING=$(TRAINING) npm run build

# ---- publish ----
publish: publish-linux

# Linux: framework-dependent publish. OBS is a system dependency; the app discovers it at runtime.
publish-linux: web
	dotnet publish $(APP_CS)/Tript.App.csproj -f net10.0 -c $(CONFIG) -r $(RID) --self-contained $(SELF_CONTAINED) \
		-p:EnableTraining=$(TRAINING) \
		-o $(PUBLISH_DIR)
	# The app host serves the built frontend from ./dist next to the binary.
	mkdir -p $(PUBLISH_DIR)/dist
	cp -r $(WEB_SRC)/dist/* $(PUBLISH_DIR)/dist/

# Windows: self-contained publish + bundled OBS.
publish-windows: restore-windows
	$(MAKE) web
	$(MAKE) obs-fetch
	rm -rf $(WIN_PUBLISH_DIR)
	mkdir -p $(WIN_APP_DIR)
	$(MAKE) publish-shell-win
	$(MAKE) assemble-windows
	$(MAKE) launcher-windows

# ---- windows assembly ----
# The OBS runtime is curated here to match exactly what the app's SafeModules allowlist loads
# (src/Tript.App/Program.cs). The allowlist is the safety net: a module that is not shipped can
# never be loaded, so slimming the bundle cannot change runtime behaviour. Every vendor's encoder
# probe ships, so the bundle is not AMD- or NVIDIA-specific — each machine's probe binary resolves
# next to the app process exe (os_get_executable_path_ptr) and registers only the encoders whose
# hardware is actually present.

# The allowlist, mirrored here (single source of truth is Program.cs; keep them in step).
OBS_MODULES := obs-x264 obs-ffmpeg obs-nvenc obs-qsv11 win-capture image-source win-wasapi

# bin/64bit's libobs + graphics module + the media dlls obs-ffmpeg.dll imports. Qt, the OBS
# frontend (obs64.exe, obs-frontend-api.dll, obs-scripting.dll, lua51.dll) and every PDB are
# dropped; av*/sw* are wildcarded so an OBS bump that renames them still copies.
OBS_BIN_CORE := obs.dll libobs-d3d11.dll libobs-winrt.dll libobs-opengl.dll \
	libx264-164.dll datachannel.dll libcurl.dll librist.dll srt.dll w32-pthreads.dll zlib.dll

assemble-windows: obs-fetch
	# Copy the built frontend as ./dist (the app host serves it from next to the binary).
	mkdir -p $(WIN_APP_DIR)/dist
	cp -r $(WEB_SRC)/dist/* $(WIN_APP_DIR)/dist/
	# Optional offline fallback. Thin releases download only models for detected games.
	mkdir -p $(WIN_APP_DIR)/data/ocr
	cp -r data/ocr/* $(WIN_APP_DIR)/data/ocr/
	@if [ "$(BUNDLE_MODELS)" = "true" ]; then \
		mkdir -p $(WIN_APP_DIR)/data/models; \
		cp -r data/models/* $(WIN_APP_DIR)/data/models/; \
	fi

	# Curated libobs runtime, mirroring the portable zip layout (bin/64bit + obs-plugins/64bit +
	# data/...) so the app's ObsRuntimeLocator finds it unchanged.
	mkdir -p $(WIN_APP_DIR)/bin/64bit
	for dll in $(OBS_BIN_CORE); do \
		cp $(OBS_EXTRACTED)/bin/64bit/$$dll $(WIN_APP_DIR)/bin/64bit/; done
	cp $(wildcard $(OBS_EXTRACTED)/bin/64bit/av*.dll) $(WIN_APP_DIR)/bin/64bit/
	cp $(wildcard $(OBS_EXTRACTED)/bin/64bit/sw*.dll) $(WIN_APP_DIR)/bin/64bit/

	# The allowlisted module binaries only — browser/CEF, Qt plugins, filters, outputs, vst,
	# decklink/aja and the like never load and are not shipped.
	mkdir -p $(WIN_APP_DIR)/obs-plugins/64bit
	for module in $(OBS_MODULES); do \
		cp $(OBS_EXTRACTED)/obs-plugins/64bit/$$module.dll $(WIN_APP_DIR)/obs-plugins/64bit/; done

	# Module data: only the shipped modules' dirs. win-capture's carries the graphics hooks
	# (graphics-hook*.dll, inject-helper*.exe) game capture needs, so it ships whole; the others
	# are locale/schema and are fine to keep or drop. data/libobs (effects) and data/obs-studio
	# are search roots the locator registers unevaluated, so both ship.
	mkdir -p $(WIN_APP_DIR)/data
	cp -r $(OBS_EXTRACTED)/data/libobs $(WIN_APP_DIR)/data/
	cp -r $(OBS_EXTRACTED)/data/obs-studio $(WIN_APP_DIR)/data/
	mkdir -p $(WIN_APP_DIR)/data/obs-plugins
	for module in $(OBS_MODULES); do \
		cp -r $(OBS_EXTRACTED)/data/obs-plugins/$$module $(WIN_APP_DIR)/data/obs-plugins/ \
			2>/dev/null || true; done
	# win-capture resolves its injection helpers beside the application executable, not from the
	# module data directory. Without these files the source repeatedly reports "init_pipe" failures.
	cp $(OBS_EXTRACTED)/data/obs-plugins/win-capture/graphics-hook*.dll $(WIN_APP_DIR)/
	cp $(OBS_EXTRACTED)/data/obs-plugins/win-capture/inject-helper*.exe $(WIN_APP_DIR)/
	cp $(OBS_EXTRACTED)/data/obs-plugins/win-capture/graphics-hook*.dll $(WIN_APP_DIR)/obs-plugins/64bit/
	cp $(OBS_EXTRACTED)/data/obs-plugins/win-capture/inject-helper*.exe $(WIN_APP_DIR)/obs-plugins/64bit/

	# The subprocess helpers the plugins spawn, resolved from the app process exe path: the muxer
	# (obs-ffmpeg) and the encoder capability probes (obs-amf-test, obs-nvenc-test, obs-qsv-test).
	# Without a probe binary the corresponding hardware encoder ids never register — a missing
	# obs-nvenc-test.exe silently leaves an NVIDIA machine with software-only encoding.
	#
	# A shell glob, not $(wildcard): make caches the directory listings it globs, and obs-fetch
	# creates these files during the same run — so a cached empty result would ship no probes at
	# all. And no "|| true" on either: both failures are silent and neither is survivable. Without
	# the muxer there is no recording; without a probe the machine quietly falls back to software
	# encoding, which is the difference between a playable capture and a slideshow.
	cp $(OBS_EXTRACTED)/bin/64bit/obs-ffmpeg-mux.exe $(WIN_APP_DIR)/
	@probes="$$(ls $(OBS_EXTRACTED)/bin/64bit/obs-*-test.exe 2>/dev/null)"; \
	if [ -z "$$probes" ]; then \
		echo "assemble-windows: no obs-*-test.exe encoder probes in $(OBS_EXTRACTED)/bin/64bit."; \
		echo "  Every hardware encoder would be absent at runtime and recordings would fall back"; \
		echo "  to x264. Refusing to assemble a bundle that silently encodes in software."; \
		exit 1; \
	fi; \
	for probe in $$probes; do \
		echo "  encoder probe: $$(basename $$probe)"; \
		cp "$$probe" $(WIN_APP_DIR)/; \
	done

launcher-windows:
	mkdir -p $(WIN_PUBLISH_DIR)/.launcher
	$(WIN_LAUNCHER_WINDRES) -I src/Tript.Web/public -O coff \
		src/Tript.Launcher/launcher.rc $(WIN_PUBLISH_DIR)/.launcher/launcher.res
	$(WIN_LAUNCHER_CC) -O2 -Wall -Wextra -Werror -municode -mwindows -static \
		src/Tript.Launcher/launcher.c $(WIN_PUBLISH_DIR)/.launcher/launcher.res \
		-o $(WIN_PUBLISH_DIR)/Tript.exe -luser32
	rm -rf $(WIN_PUBLISH_DIR)/.launcher

# ---- shell (desktop window) ----
# Publish the desktop shell (Photino webview) next to the app host. The shell reuses the app host's
# construction seam and serves the same UI; it needs webkit2gtk-4.1 on Linux (see README).
publish-shell:
	dotnet publish src/Tript.Shell/Tript.Shell.csproj -f net10.0 -c $(CONFIG) -r $(RID) \
		--self-contained $(SELF_CONTAINED) -p:EnableTraining=$(TRAINING) \
		-o $(PUBLISH_DIR)
	# The shell resolves its UI root from ./dist next to the binary (DefaultWebRoot prefers the
	# published layout), so the built frontend must ship into the publish folder here — a shell
	# build must not depend on a prior publish-linux having populated it.
	mkdir -p $(PUBLISH_DIR)/dist
	cp -r $(WEB_SRC)/dist/* $(PUBLISH_DIR)/dist/

# Windows variant of publish-shell: publish the desktop shell under App/ so the package root exposes
# only the native Tript.exe launcher. Photino.Native 4.0.22 ships its
# win-x64 payload (Photino.Native.dll + WebView2Loader.dll) via runtimes/win-x64/native, which
# self-contained win-x64 publish lands automatically.
publish-shell-win: restore-windows
	dotnet publish src/Tript.Shell/Tript.Shell.csproj -f net10.0 -c $(CONFIG) -r win-x64 \
		--self-contained true -p:EnableTraining=$(TRAINING) \
		-p:RestoreLockedMode=true -o $(WIN_APP_DIR)
	rm -f $(WIN_APP_DIR)/Tript.App.exe $(WIN_APP_DIR)/Tript.App.deps.json \
		$(WIN_APP_DIR)/Tript.App.runtimeconfig.json
	# Keep the Windows publish self-contained too; dotnet publish does not build or copy the Vite UI.
	mkdir -p $(WIN_APP_DIR)/dist
	cp -r $(WEB_SRC)/dist/* $(WIN_APP_DIR)/dist/

# The per-project lock files carry the ordinary framework graph in source control. Windows publishing
# needs the additional RID graph for Photino.Native and OBS assets, so materialize it deliberately before
# the locked publish rather than letting an ordinary test restore rewrite it implicitly.
restore-windows:
	dotnet restore src/Tript.App/Tript.App.csproj -p:RuntimeIdentifier=win-x64 -p:RestoreForceEvaluate=true --nologo
	dotnet restore src/Tript.Shell/Tript.Shell.csproj -p:RuntimeIdentifier=win-x64 -p:RestoreForceEvaluate=true --nologo

shell: web publish-shell
	@echo "Shell built at: $(PUBLISH_DIR)/Tript.Shell"
	@echo "Run it with:    make run   (or ./Tript.Shell directly)"

# ---- OBS download ----
obs-fetch: $(OBS_ARCHIVE) $(OBS_EXTRACTED)

$(OBS_ARCHIVE):
	mkdir -p third_party
	curl -L --fail -o $(OBS_ARCHIVE) $(OBS_URL)

$(OBS_EXTRACTED): $(OBS_ARCHIVE)
	rm -rf $(OBS_EXTRACTED)
	mkdir -p $(OBS_EXTRACTED)
	cd $(OBS_EXTRACTED) && unzip -q ../$(notdir $(OBS_ARCHIVE))
	touch $(OBS_EXTRACTED)

# ---- aliases ----
dev: publish-linux
	$(MAKE) run

release:
	@echo "Building the release app (headless host + desktop shell)..."
	$(MAKE) CONFIG=Release TRAINING=$(TRAINING) linux publish-shell
	@echo ""
	@echo "Built the release app at: $(DIST_DIR)/Release/"
	@echo "Run it with:              make run CONFIG=Release   (prefers the native window)"
	@echo "Or run the window directly: ./$(DIST_DIR)/Release/Tript.Shell"
	@echo "The headless host (prints the UI URL) is: ./$(DIST_DIR)/Release/Tript.App"

linux: publish-linux

windows: publish-windows

# ---- run ----
# The app host is headless: it prints READY on stdout and serves its UI over HTTP. There is no
# window; the URL is printed on stdout and no browser is opened. Use FAKE_RECORDER=false to record
# with real OBS (needs a display server and a system obs-studio install). `run` prefers the desktop
# shell (Tript.Shell) when it has been built (make shell) and falls back to the headless host
# otherwise.
run:
	@if [ -x "$(SHELL_BIN)" ]; then \
		echo "Launching desktop shell..."; \
		cd $(PUBLISH_DIR) && ./Tript.Shell $$([ "$(FAKE_RECORDER)" = "true" ] && echo --fake-recorder); \
	else \
		echo "No shell binary at $(SHELL_BIN); falling back to headless host."; \
		cd $(PUBLISH_DIR) && ./Tript.App $$([ "$(FAKE_RECORDER)" = "true" ] && echo --fake-recorder) \
			& echo "Tript is up — open the URL on the host's own READY line above (Ctrl-C to stop)."; \
		echo "The bare http://localhost:8892/ is refused: the UI needs the per-launch key on that line."; \
		wait; \
	fi

# ---- test ----
# `test` is the gate that is meant to be green on any developer machine: the .NET unit suites plus
# the frontend. The OBS integration suite is NOT in it — see `test-integration` below for why.
#
# Both halves always run, and the exit code reflects both. They used to be two recipe lines, which
# meant make stopped at the first failure and the entire frontend suite was silently skipped
# whenever anything on the .NET side failed.
UNIT_TEST_PROJECTS := tests/Tript.App.Tests tests/Tript.Detection.Tests tests/Tript.Media.Tests \
	tests/Tript.Recorder.Tests tests/Tript.Settings.Tests tests/Tript.GameDiscovery.Tests

test:
	@fail=0; \
	$(MAKE) --no-print-directory test-dotnet || fail=1; \
	$(MAKE) --no-print-directory test-web || fail=1; \
	if [ $$fail -ne 0 ]; then echo ""; echo "make test: FAILED"; fi; \
	exit $$fail

# -m:1 serializes the test projects. App.Tests drives a real libobs context (real recordings spawn
# obs-ffmpeg-mux helpers), and running testhosts in parallel trips an intermittent libobs JSON-parse
# crash. Serializing keeps one libobs workload in the process tree at a time.
# A loop rather than one invocation over the solution: it keeps the integration suite out by naming
# what is in, and one failing project no longer hides the results of the ones after it.
test-dotnet:
	@fail=0; \
	for project in $(UNIT_TEST_PROJECTS); do \
		dotnet test $$project -c $(CONFIG) -f net10.0 -p:EnableTraining=$(TRAINING) --nologo -m:1 || fail=1; \
	done; \
	exit $$fail

# The typecheck is not optional: vitest strips types rather than checking them, so without this a
# type error passes `make test` and only surfaces at `make web`.
test-web:
	cd $(WEB_SRC) && [ -d node_modules ] || (cd $(WEB_SRC) && npm ci)
	cd $(WEB_SRC) && npx tsc -b --noEmit
	cd $(WEB_SRC) && VITE_TRIPT_TRAINING=$(TRAINING) npx vitest run

# The OBS binding's integration suite. Kept out of `test` because it needs more than a checkout:
# OBS >= 30.1 (the binding P/Invokes entry points absent from earlier builds), a display server, and
# the obs-ffmpeg-mux helper. On a machine that does not have them it fails for reasons that have
# nothing to do with the change under test, which is exactly how a gate stops being read.
test-integration:
	dotnet test tests/Tript.Obs.IntegrationTests -c $(CONFIG) -f net10.0 -p:EnableTraining=$(TRAINING) --nologo -m:1

test-all: test test-integration

# ---- clean ----
clean:
	rm -rf $(DIST_DIR) third_party
	dotnet clean Tript.slnx 2>/dev/null || true
	cd $(WEB_SRC) && rm -rf dist 2>/dev/null || true
