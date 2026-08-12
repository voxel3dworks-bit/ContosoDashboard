<!--
SYNC IMPACT REPORT
==================
Version change: 0.0.0 -> 1.0.0
Bump Type: MAJOR
Reasoning: Initial ratification of the ContosoDashboard constitution, replacing all template placeholders with concrete principles and guidelines derived from the project architecture and stakeholder requirements.

Modified Principles:
- [PRINCIPLE_1_NAME] -> I. Offline-First Architecture with Cloud Migration Path
- [PRINCIPLE_2_NAME] -> II. Service-Level Security and IDOR Prevention
- [PRINCIPLE_3_NAME] -> III. Spec-Driven Development (SDD) (NON-NEGOTIABLE)
- [PRINCIPLE_4_NAME] -> IV. Asynchronous & Performance-Focused Data Access
- [PRINCIPLE_5_NAME] -> V. Safe File Storage & Upload Sequence

Added Sections:
- Core Principles (fully populated from template)
- Additional Technical Constraints & Security Standards (populated from [SECTION_2])
- Development Workflow & Quality Gates (populated from [SECTION_3])
- Governance (fully populated from template)

Removed Sections:
- None

Follow-up TODOs:
- None (All placeholders successfully resolved)
-->

# ContosoDashboard Constitution

## Core Principles

### I. Offline-First Architecture with Cloud Migration Path
All features MUST be designed and implemented to run fully offline using local resources (such as SQL Server LocalDB and local filesystem storage). However, all infrastructure dependencies MUST be isolated behind clean interface abstractions (e.g., `IFileStorageService`). This ensures that the application can be seamlessly migrated to cloud services (such as Azure SQL Database and Azure Blob Storage) purely via dependency injection configuration, with zero changes required to core business logic or UI layers.

### II. Service-Level Security and IDOR Prevention
Security MUST be enforced using a defense-in-depth approach. Authentication is cookie-based, and user identity is claims-based with strict role-based access control (RBAC). All data access and manipulation operations MUST undergo service-level authorization checks. Direct database lookups by record ID MUST be validated against the active user’s permissions to prevent Insecure Direct Object Reference (IDOR) vulnerabilities, ensuring rigorous user isolation.

### III. Spec-Driven Development (SDD) (NON-NEGOTIABLE)
The development lifecycle MUST follow the Spec-Driven Development methodology using the GitHub Spec Kit. No code changes, feature branches, or implementation phases may begin until feature specifications (`.specify/`) and implementation plans are formally drafted, reviewed, and approved. Automated unit and integration tests MUST accompany all new features and bug fixes to verify correctness and prevent regression, maintaining clear traceability of requirements.

### IV. Asynchronous & Performance-Focused Data Access
To prevent blocking UI operations in Blazor Server and ensure scalability, all database queries and file storage actions MUST be implemented using the asynchronous `async/await` pattern. Entity Framework Core queries MUST employ eager loading (`.Include()`) for related entities to prevent N+1 query performance problems. Database indexes MUST be placed on frequently queried or filtered fields, and long-running operations must be optimized or backgrounded.

### V. Safe File Storage & Upload Sequence
All uploaded files MUST be stored outside of the web-accessible `wwwroot` directory to prevent unauthorized execution or path traversal. Filenames MUST be converted to GUIDs immediately upon upload. The upload sequence MUST strictly follow this order: (1) Generate unique GUID-based path, (2) Save file to the secure local directory, and (3) Save the document metadata to the database. This precise sequence prevents orphaned database records or orphaned files, ensuring absolute consistency.

## Additional Technical Constraints & Security Standards
The technology stack is restricted to ASP.NET Core 8.0, C#, and Blazor Server with Entity Framework Core. To support diverse Office documents, database fields storing MIME types MUST be designed to accommodate up to 255 characters. For security and to protect against path traversal and malware, all uploaded file extensions MUST be checked against a strict whitelist (e.g., PDF, MS Office docs, images, txt), and file sizes MUST be validated to not exceed the 25 MB limit before processing.

## Development Workflow & Quality Gates
Before initiating testing of any database or file storage feature, developers MUST ensure a clean state by dropping and recreating the LocalDB database (e.g., via `dotnet ef database drop --force` or stopping and deleting LocalDB instances) to prevent duplicate key violations and orphaned records from prior runs. In Blazor UI, the `InputFile` component MUST utilize the `@key` attribute to force re-renders, and the file streams MUST be copied to memory and cleared immediately to prevent memory leaks or disposal errors.

## Governance
This Constitution is the supreme governing document of the ContosoDashboard repository and supersedes all general local coding practices. Every pull request and code review MUST actively verify absolute compliance with these principles. Any proposed architectural divergence or modification to this Constitution requires a formal amendment request, documentation of rationale, a detailed migration plan, and approval by team consensus.

**Version**: 1.0.0 | **Ratified**: 2026-08-11 | **Last Amended**: 2026-08-11