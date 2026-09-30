# Wallet Service
![ci](badge-url)

A MYR e-wallet backend with a double-entry ledger, transactional outbox, and Kafka-driven notifications.
ASP.NET Core 10 · PostgreSQL · Kafka · EF Core · Testcontainers

## Architecture
(mermaid diagram from docs/guide/00-overview.md)

## What makes it interesting
- Double-entry ledger: every movement is a balanced journal entry; SUM(postings) = 0 is tested.
- No double-spend: row-level locks in a consistent order; a 20-way concurrency test proves it.
- Idempotent money movement via Idempotency-Key.
- No lost events: transactional outbox; the API keeps working while Kafka is down.
- Idempotent consumer: replaying the whole topic creates zero duplicate notifications.

## Run it
docker compose --profile app up --build
then open requests.http / http://localhost:5080/scalar

## API
(table of endpoints)

## Design decisions & trade-offs
(short: pessimistic vs optimistic locking, at-least-once, hot system account, what you'd do at scale)

## What I'd do next
(the stretch goals below that you *didn't* do; this shows you understand the gaps)