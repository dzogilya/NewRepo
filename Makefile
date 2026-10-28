.PHONY: up down logs test lint demo clean

up:
	docker compose up --build -d

down:
	docker compose down

logs:
	docker compose logs -f

test:
	dotnet test Fractals.slnx

lint:
	dotnet format --verify-no-changes

demo:
	chmod +x ./scripts/demo_balancing.sh && ./scripts/demo_balancing.sh

clean:
	dotnet clean Fractals.slnx

