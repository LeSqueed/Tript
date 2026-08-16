# Tript build & packaging Makefile.
#
# Targets:
#   all        build the current OS (Debug) — the dev loop
#   dev        build + run the app (Debug, --fake-recorder by default for a no-hardware dev session)
#   release    build the current OS in Release
#   linux      publish a framework-dependent Linux build into dist/<config>
#   windows    publish a Windows build with the pinned OBS bundle into dist/<config>-win
#   obs-fetch  download the pinned OBS Studio Windows portable zip into third_party/
#   test       run the .NET test suite + the frontend Vitest suite
#   run        run the assembled binary (linux target)
#   clean      remove build outputs
#
# Flags (defaults):
#   CONFIG=Debug|Release          build configuration (default Debug)
#   RID=linux-x64|win-x64         runtime identifier (default linux-x64)
#   OBS_VERSION=32.2.2            pinned OBS Studio Windows version
#   SELF_CONTAINED=false          self-contained .NET publish (Windows bundle defaults true)
#   DIST_DIR=dist                 output directory for assembled builds
#   FAKE_RECORDER=true|false      pass --fake-recorder to `run` (default true for dev)
#   OBS_URL=<url>                 override the OBS zip download URL
#
# The Linux build is framework-dependent and expects OBS as a system dependency (the app
# discovers it at runtime). The Windows build bundles a pinned OBS Studio portable zip.
# Both assemble the built frontend (src/Tript.Web/dist) into the publish folder as ./dist so the
# app host serves its own UI from next to the binary.

CONFIG ?= Debug
RID ?= linux-x64
OBS_VERSION ?= 32.2.2
SELF_CONTAINED ?= false
DIST_DIR ?= dist
FAKE_RECORDER ?= true
OBS_URL ?= https://github.com/obsproject/obs-studio/releases/download/$(OBS_VERSION)/OBS-Studio-$(OBS_VERSION)-Windows-x64.zip

# ---- derived paths ----
WEB_SRC := src/Tript.Web
APP_CS := src/Tript.App
PUBLISH_DIR := $(DIST_DIR)/$(CONFIG)
WIN_PUBLISH_DIR := $(DIST_DIR)/$(CONFIG)-win
OBS_ARCHIVE := third_party/obs-studio-$(OBS_VERSION).zip
OBS_DIR := third_party/obs-studio-$(OBS_VERSION)
OBS_EXTRACTED := third_party/obs-studio-$(OBS_VERSION)-x64

.PHONY: all dev release linux windows obs-fetch test run clean
.PHONY: web frontend publish publish-linux publish-windows assemble-windows

all: linux

# ---- web (frontend) ----
web frontend:
	cd $(WEB_SRC) && npm ci && npm run build

# ---- publish ----
publish: publish-linux

# Linux: framework-dependent publish. OBS is a system dependency; the app discovers it at runtime.
publish-linux: web
	dotnet publish $(APP_CS)/Tript.App.csproj -f net10.0 -c $(CONFIG) -r $(RID) --self-contained $(SELF_CONTAINED) \
		-o $(PUBLISH_DIR)
	# The app host serves the built frontend from ./dist next to the binary.
	mkdir -p $(PUBLISH_DIR)/dist
	cp -r $(WEB_SRC)/dist/* $(PUBLISH_DIR)/dist/

# Windows: self-contained publish + bundled OBS.
publish-windows:
	$(MAKE) web
	$(MAKE) obs-fetch
	dotnet publish $(APP_CS)/Tript.App.csproj -f net10.0 -c $(CONFIG) -r win-x64 --self-contained true \
		-o $(WIN_PUBLISH_DIR)
	$(MAKE) assemble-windows

# ---- windows assembly ----
assemble-windows: obs-fetch
	# Copy the built frontend as ./dist (the app host serves it from next to the binary).
	mkdir -p $(WIN_PUBLISH_DIR)/dist
	cp -r $(WEB_SRC)/dist/* $(WIN_PUBLISH_DIR)/dist/
	# Bundle the OBS runtime, mirroring the portable zip layout at the publish root so the app's
	# Windows locator finds it: bin/64bit (obs.dll + obs-ffmpeg-mux.exe), obs-plugins/64bit,
	# data/obs-plugins, data/obs-studio.
	mkdir -p $(WIN_PUBLISH_DIR)/bin/64bit
	cp -r $(OBS_EXTRACTED)/bin/64bit/. $(WIN_PUBLISH_DIR)/bin/64bit/
	mkdir -p $(WIN_PUBLISH_DIR)/obs-plugins/64bit
	cp -r $(OBS_EXTRACTED)/obs-plugins/64bit/. $(WIN_PUBLISH_DIR)/obs-plugins/64bit/
	mkdir -p $(WIN_PUBLISH_DIR)/data
	cp -r $(OBS_EXTRACTED)/data/obs-plugins $(WIN_PUBLISH_DIR)/data/
	cp -r $(OBS_EXTRACTED)/data/obs-studio $(WIN_PUBLISH_DIR)/data/
	# The ffmpeg_muxer plugin spawns obs-ffmpeg-mux.exe next to the app process binary
	# (os_get_executable_path_ptr → /proc/self/exe on Windows, the process exe path). It is copied
	# to the publish root alongside Tript.App.exe.
	cp $(OBS_EXTRACTED)/bin/64bit/obs-ffmpeg-mux.exe $(WIN_PUBLISH_DIR)/ 2>/dev/null || true

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
	@echo "Building the release binary..."
	$(MAKE) CONFIG=Release linux
	@echo ""
	@echo "Built the release binary at: $(DIST_DIR)/Release/Tript.App"
	@echo "Run it with:                make run CONFIG=Release"
	@echo "Then open the UI at:        http://localhost:2882/"

linux: publish-linux

windows: publish-windows

# ---- run ----
# The app is a headless host: it prints READY on stdout and serves its UI over HTTP. There is no
# window; it opens the default browser at the UI URL itself (best-effort), and the URL is printed
# too in case that does not work. Use FAKE_RECORDER=false to record with real OBS (needs a display
# server and a system obs-studio install).
run:
	cd $(PUBLISH_DIR) && ./Tript.App $$([ "$(FAKE_RECORDER)" = "true" ] && echo --fake-recorder) \
		& echo "Tript is up — open http://localhost:2882/ (Ctrl-C to stop)"; wait

# ---- test ----
test:
	# -m:1 serializes the test projects. The Obs integration suite and the App.Tests smoke tests
	# both drive a real libobs context (real recordings spawn obs-ffmpeg-mux helpers), and running
	# their testhosts in parallel trips an intermittent libobs JSON-parse crash. Serializing keeps
	# one libobs workload in the process tree at a time.
	dotnet test Tript.slnx -c $(CONFIG) -f net10.0 --nologo -m:1
	cd $(WEB_SRC) && npx vitest run

# ---- clean ----
clean:
	rm -rf $(DIST_DIR) third_party
	dotnet clean Tript.slnx 2>/dev/null || true
	cd $(WEB_SRC) && rm -rf dist 2>/dev/null || true
