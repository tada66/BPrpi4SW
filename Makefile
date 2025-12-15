# Makefile for BPrpi4SW

# Configuration variables
PROJECT = BPrpi4SW
CONFIG = Release
FRAMEWORK = net10.0
RUNTIME = linux-x64
OUTPUT_DIR = ./src/bin/$(CONFIG)/$(FRAMEWORK)/$(RUNTIME)/publish

# Build the project
build:
	dotnet publish src/$(PROJECT).csproj -c $(CONFIG) -r $(RUNTIME) \
	--self-contained true \
	-p:PublishSingleFile=true \
	-p:PublishTrimmed=true \
	-o .

.PHONY: build