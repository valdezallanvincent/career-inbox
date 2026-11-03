# Personal portfolio case study

## Context

I built Career Inbox for my own developer job search, with AI coding assistance. It is a personal business-process automation project, not paid client experience. It connects n8n forms and scheduling to a C#/.NET service and a private SQLite database.

## Problem and solution

Manually tracking listings across job boards creates duplicate records and makes it easy to lose application status. The system captures listings, validates them, applies transparent matching rules, deduplicates tracking URLs, records status updates and produces a daily shortlist with follow-up reminders.

## Demo in five minutes

1. Show the three n8n workflows: capture, status, and daily shortlist.
2. Submit a fictional .NET/Azure job with a USD hourly rate and explicit eligibility/status.
3. Show the returned ID and explained score.
4. Submit the same URL with a different tracking parameter: demonstrate that no second row is created.
5. Submit a missing title or negative rate to demonstrate rejection.
6. Record an application status; explain that this records a manual action, not an auto-application.
7. Generate the digest and show the private report.
8. Show the automated integration tests and discuss limitations.

## What I can truthfully claim after reviewing and running it

“I have a personal automation project that uses n8n and a .NET API to organize my job search. It validates and deduplicates opportunities, ranks them using explicit rules, tracks application status, and generates a scheduled shortlist. The repository includes integration tests, operational documentation, and CI. I developed it with AI assistance and can walk through the implementation.”

This release is rule-based business automation. Do not describe it as an AI agent, LLM workflow, client deployment, or proof of a year of automation experience. Review the code and be prepared to explain transactions, duplicate handling, localhost deployment, HTTP failures and scoring limitations before using it in an interview.

No time savings, accuracy percentages or client results are claimed without measured evidence. An optional AI extraction feature should be introduced only with structured outputs, a representative evaluation set, cost tracking and human review of uncertain results.
