# Project Constitution

## Stack

- .NET 10, C#, Minimal API
- Persistence via PostgreSQL using EF Core
- Testing with xUnit
- Vue 3 + TypeScript for the frontend
- Use stylex for CSS-in-JS styling

## Architecture

- Endpoints do not access the database directly; they must always go through an application service
- Business logic does not reside in the endpoint or the HTTP handler
- No new dependencies may be added without a written justification in the plan

## Quality

- Every business rule must have a corresponding unit test
- Every endpoint must have at least one "happy path" integration test
- Validation errors must always return a 400 status code with `ProblemDetails`
- Bugs must be fixed and accompanied by a regression test to prevent recurrence

## Conventions

- Commits must follow the format: `type(scope): description`, including the requirement identifier

## Governance

- If the plan contradicts the constitution, the plan must be adjusted
- If the constitution needs to be changed, such changes must be made in a separate commit
