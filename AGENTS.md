# Documentation language

Write all project documentation in English, including README files, architecture documents, third-party notices, and supporting text assets.

Preserve code identifiers, file paths, URLs, and verbatim upstream license notices when editing documentation.

# UI localization

All application-owned user interface text must support English (`en-US`), Brazilian Portuguese (`pt-BR`), and Spanish (`es-ES`). Use the shared localization catalogs for navigation, labels, dialogs, validation and error messages, tooltips, accessibility text, notifications, and tray menus. Add complete translations for all three languages whenever UI text is introduced or changed; do not hardcode user-facing strings in views or application logic.

Respect the selected UI culture for numbers, dates, and durations, and keep open views responsive to language changes. Preserve user-entered profile names, groups, hostnames, paths, filenames, protocol identifiers, and other user data. Windows-owned dialog controls may follow the operating system language; all application-provided titles and messages must still be localized.

Project documentation, developer-script output, and diagnostic logs remain in English regardless of the selected UI language. UI translations must not weaken credential masking, disclosure verification, or sanitization of errors and logs.

# Public repository data

Use fictional, neutral data in tests, examples, documentation, and committed screenshots. Never copy a contributor's or user's actual account name, home directory, hostname, server address, credentials, or other personal environment details into project fixtures or published assets. Use clearly synthetic identities such as `test-user` and reserved example domains. Keep machine-specific verification reports and captures in ignored local output directories; review any artifact separately before publishing it. Preserve legitimate upstream license notices and intentional public project attribution.
