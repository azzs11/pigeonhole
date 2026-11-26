# Changelog

## [1.0.0](https://github.com/azzs11/pigeonhole/compare/v0.6.0...v1.0.0) (2026-11-26)


### Features

* **bench:** LiveAgentBench, a live Claude model driving the agent endpoint ([bd92617](https://github.com/azzs11/pigeonhole/commit/bd92617febb02ee22cda41c3ce6099ab9ec564a0))
* **bench:** MailChaos harness, fault injectors and three-system comparison ([4b20778](https://github.com/azzs11/pigeonhole/commit/4b20778fb967ed5629900705fafbbf153a7a8dac))
* **delivery:** outbox relay, SMTP/Resend/SES providers, failover and unknown-outcome state ([04107f8](https://github.com/azzs11/pigeonhole/commit/04107f839c368403834002e1ea39f077f84a40e7))
* durable core with idempotent sends, hashed API keys and tenant scoping ([46b1dfe](https://github.com/azzs11/pigeonhole/commit/46b1dfece42f2ab0e4daed50142bb74f0163dad3))
* **guard:** Agent Send Guard with approvals and audit, MCP server, AgentMailBench ([2d17c0f](https://github.com/azzs11/pigeonhole/commit/2d17c0f6ef11ec59982570bd8bc0087b5c997c1b))
* **obs:** OpenTelemetry traces and metrics, masked JSON logs, Grafana dashboards, explain endpoint ([6d889c0](https://github.com/azzs11/pigeonhole/commit/6d889c09faa29a1a3ecf1dd25b8481361c7a4421))
* **ops:** dashboard API, provider health, sandbox faults; fix OpenAPI document ([0b19e22](https://github.com/azzs11/pigeonhole/commit/0b19e22aeac65d7dea9f0c6e0e6de1c5b07ce4fc))
* **sandbox:** self-service demo key, sink-only worker, trusted proxy IPs ([2aafecc](https://github.com/azzs11/pigeonhole/commit/2aafecc6944f81b3661db596ac0c56efedc8e3f1))
* signed provider webhooks, suppression, atomic quotas, rate limiting and reputation guard ([f551cc1](https://github.com/azzs11/pigeonhole/commit/f551cc13c571127a482e9169900ccec8c5fbf1b9))
* **web:** chocolate-shop redesign with motion ([69106b2](https://github.com/azzs11/pigeonhole/commit/69106b258fe1bb6cd49863d58cc55ccc90245364))
* **web:** sorting-office dashboard with Playwright flow and axe checks ([9bc7d05](https://github.com/azzs11/pigeonhole/commit/9bc7d052344dd46671706af8f32c76ae9904858e))


### Bug Fixes

* **bench:** stop MailChaos webhook consumer stalls; 10,000 messages per run, multi-seed results ([5d981f6](https://github.com/azzs11/pigeonhole/commit/5d981f607f88d6cee41c45ad19178a4bdc209446))
* **ci:** gate guard p99 in the benchmark, not the parallel test suite ([303e7c5](https://github.com/azzs11/pigeonhole/commit/303e7c5ff76df860f882dd7e57e41e32393c9aad))
* **ci:** update MailChaos lock file for the Guard reference ([8294431](https://github.com/azzs11/pigeonhole/commit/829443143f2341aa49b96987e4d1cb9b898199b7))
* **tests:** generate webhook signing keys at runtime; add gitleaks to CI ([be63ee3](https://github.com/azzs11/pigeonhole/commit/be63ee3eca9e9c9089cb96bbbd8faef1b80fd773))


### Performance Improvements

* index-only idempotency lookup, charge quota last, size connection pools ([e763a80](https://github.com/azzs11/pigeonhole/commit/e763a80ee85b744adae801d6685a591a862b29ea))
