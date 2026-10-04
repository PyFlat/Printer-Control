PROJECT  := src/PrinterControl
MANIFEST := $(PROJECT)/manifest.json

STATE    := $(PROJECT)/.macrodeck-dev-state

UTF8     := $(if $(filter Windows_NT,$(OS)),chcp.com 65001 >/dev/null &&)
RUN      := $(UTF8) macrodeck-plugin run --project $(PROJECT) --state-directory $(STATE)

SDK      := grep 'MacroDeckSdkVersion Condition' Directory.Packages.props | cut -d'>' -f2 | cut -d'<' -f1
TESTS    := dotnet test PrinterControl.slnx --configuration Release

RID      := $(if $(filter Windows_NT,$(OS)),win-x64,$(if $(filter Darwin,$(shell uname -s)),osx-arm64,linux-x64))

# Store images: every [UiPreview] scenario at each deck shape. Override on the command line,
# e.g. make preview CELLS="--cells 2x2" PREVIEW_ARGS="--theme light". STORE=1 then pads each one onto a
# 16:9 canvas, the shape the store crops card artwork to.
CELLS    := --cells 1x1 --cells 2x1 --cells 2x2
PREVIEWS := artifacts/previews

# An unreleased SDK: a Macro Deck checkout packed into local-feed/, built with -p:MacroDeckSdkVersion=$(LOCAL_SDK).
MACRODECK ?= ../Macro-Deck
LOCAL_SDK ?= 3.0.0-local.1
SDK_PROJECTS := protocol/src/MacroDeck.Plugin.Protocol ui-model/src/MacroDeck.Ui.Model ui-model/src/MacroDeck.Ui \
	sdk/src/MacroDeck.Localization sdk/src/MacroDeck.Sdk sdk/src/MacroDeck.Plugin.Analyzers \
	sdk/src/MacroDeck.Plugin.Packaging sdk/src/MacroDeck.Plugin.Hosting sdk/src/MacroDeck.Plugin.Serilog \
	sdk/src/MacroDeck.Plugin.Testing

.DEFAULT_GOAL := help
.PHONY: help cli build test run watch stub preview pack conformance update release local-sdk

help:
	@echo "make cli            install/update the macrodeck-plugin CLI to the SDK version ($$($(SDK)))"
	@echo "make build          build the solution"
	@echo "make test           unit and end-to-end tests, as the release workflow runs them"
	@echo "make run            run the plugin against the running Macro Deck"
	@echo "make watch          the same, with hot reload / restart on every saved change"
	@echo "make stub           run the plugin against a disposable stub host (no Macro Deck needed)"
	@echo "make preview [STORE=1]"
	@echo "                    render the widget previews to PNGs in $(PREVIEWS)/ (store images; STORE=1 pads to 16:9)"
	@echo "make pack           build this platform's .macroDeckPlugin ($(RID)) into artifacts/ and inspect it"
	@echo "make conformance    run the conformance suite, report in conformance.md"
	@echo "make local-sdk      pack the SDK from a Macro Deck checkout (MACRODECK=$(MACRODECK)) into local-feed/"
	@echo "make update         bump every package to its newest release (review the diff)"
	@echo "make release VERSION=x.y.z"
	@echo "                    test + pack, bump manifest.json, commit, tag vx.y.z, push"

cli:
	dotnet tool update --global MacroDeck.Plugin.Cli --version "$$($(SDK))"

build:
	dotnet build PrinterControl.slnx

test:
	$(TESTS)

run:
	$(RUN)

watch:
	$(RUN) --watch

stub:
	$(UTF8) macrodeck-plugin run --project $(PROJECT) --stub-host

preview:
	rm -rf $(PREVIEWS)
	$(UTF8) macrodeck-plugin preview render --project $(PROJECT) $(CELLS) --output $(PREVIEWS) $(PREVIEW_ARGS)
	$(if $(STORE),dotnet run tools/StoreCanvas.cs -- $(PREVIEWS))

pack:
	rm -f artifacts/*.macroDeckPlugin
	macrodeck-plugin build --source $(PROJECT) --rid $(RID) --output ./artifacts
	macrodeck-plugin inspect --artifact "$$(ls artifacts/*.macroDeckPlugin)"

conformance:
	macrodeck-plugin test --project $(PROJECT) --report markdown --output conformance.md

local-sdk:
	@test -d "$(MACRODECK)/sdk" || { echo "no Macro Deck checkout at $(MACRODECK)"; exit 1; }
	for project in $(SDK_PROJECTS); do \
	  dotnet pack "$(MACRODECK)/$$project" -c Release -p:Version=$(LOCAL_SDK) -o "$(CURDIR)/local-feed" || exit 1; \
	done

update:
	dotnet package update

release:
	@case "$(VERSION)" in \
	  [0-9]*.[0-9]*.[0-9]*) ;; \
	  *) echo "usage: make release VERSION=x.y.z (current: $$(sed -n 's/^  "version": "\(.*\)",$$/\1/p' $(MANIFEST)))"; exit 1 ;; \
	esac
	@test "$$(git rev-parse --abbrev-ref HEAD)" = main || { echo "release from main only"; exit 1; }
	@test -z "$$(git status --porcelain)" || { echo "working tree is not clean"; exit 1; }
	@! git rev-parse -q --verify "refs/tags/v$(VERSION)" >/dev/null || { echo "tag v$(VERSION) already exists"; exit 1; }
	git pull --ff-only
	$(TESTS)
	$(MAKE) pack
	sed -i 's/^  "version": ".*",$$/  "version": "$(VERSION)",/' $(MANIFEST)
	@grep -q '^  "version": "$(VERSION)",$$' $(MANIFEST) || { echo "could not set the version in $(MANIFEST)"; git checkout -- $(MANIFEST); exit 1; }
	git commit -m "chore: bump version" -- $(MANIFEST)
	git tag v$(VERSION)
	git push --atomic origin main v$(VERSION)
