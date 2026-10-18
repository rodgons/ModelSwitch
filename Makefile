# One-command local run of the Compose stack (docs/deployment.md section 2).
#   make            first run: creates .env with secrets and an Owner, builds, starts, seeds the demo, prints an API Key
#   make up         build and start only
#   make dev        hot reload: the api, worker and dashboard run on the host, http://localhost:5173 (scripts/dev.sh)
#   make down       stop (keeps the database);  make clean  also deletes the volumes
# PROFILES selects Compose profiles, e.g. `make PROFILES="demo ollama"` for the local Fallback model.

PROFILES ?= demo
COMPOSE := docker compose $(foreach p,$(PROFILES),--profile $(p))

.DEFAULT_GOAL := run
.PHONY: run setup up seed dev down clean logs ps test

run: up seed
	@echo "Dashboard: http://localhost:$$(grep -E '^MODELSWITCH_HTTP_PORT=' .env | cut -d= -f2 || echo 8080)"
	@echo "Owner:     $$(grep -E '^MODELSWITCH__Bootstrap__OwnerEmail=' .env | cut -d= -f2-) (password in .env)"

# .env from the example, generated secrets, and an Owner (git e-mail, random password) when none is set.
# compose.override.yaml publishes PostgreSQL and Valkey on localhost for dotnet run and database tools.
setup: .env compose.override.yaml

.env:
	cp .env.example .env
	./scripts/generate-secrets.sh
	@grep -qE '^MODELSWITCH__Bootstrap__OwnerEmail=.+' .env || \
		sed -i.bak -e "s|^MODELSWITCH__Bootstrap__OwnerEmail=.*|MODELSWITCH__Bootstrap__OwnerEmail=$$(git config user.email)|" .env
	@grep -qE '^MODELSWITCH__Bootstrap__OwnerPassword=.+' .env || \
		sed -i.bak -e "s|^MODELSWITCH__Bootstrap__OwnerPassword=.*|MODELSWITCH__Bootstrap__OwnerPassword=$$(openssl rand -hex 12)|" .env
	@rm -f .env.bak

compose.override.yaml:
	cp deploy/compose.override.example.yaml compose.override.yaml

up: setup
	$(COMPOSE) up -d --build --wait

# Idempotent, apart from creating one more API Key per run.
seed:
	@echo "API Key: $$(./scripts/seed-demo.sh)"

# Hot-reload loop: PostgreSQL and Valkey in Compose, the api (dotnet watch), worker, mock and Vite on the host (scripts/dev.sh).
dev: setup
	./scripts/dev.sh

down:
	$(COMPOSE) down

clean:
	$(COMPOSE) down -v

logs:
	$(COMPOSE) logs -f

ps:
	$(COMPOSE) ps -a

test:
	dotnet test ModelSwitch.slnx
	cd web && pnpm install && pnpm test
