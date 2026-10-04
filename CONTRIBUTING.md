# Contributing to OpenVersus

Thanks for your interest in contributing to OpenVersus! This project is community-maintained and we welcome contributions of all kinds.

## Getting Started

- Make sure you have Node.js installed
- Fork the repository and clone it locally
- Check if any open Issues need work before starting something new

## How to Contribute

**Reporting Bugs**

Open an Issue on GitHub with a clear description of the problem, steps to reproduce it, and your environment details.

**Suggesting Features**

Open an issue tagged `enhancement` and describe what you'd like to see, and why/how it would benefit the community.

**Submitting Code**
1. Fork the repo and create a new branch from the default branch of the repo. This will typically be `main`, `openversus`, or similar. Please check the the specific repo to determine the default branch name.
2. Make your changes
3. *Test your changes locally before submitting*
4. Open a pull request with a clear description of what you changed and why

## Building

`./build.sh` builds everything: the tests, the P2P node players run beside the game (`out/node-win-x64/`,
`out/node-linux-x64/`), the rendezvous service the nodes pair through (`out/rendezvous-linux-x64/`, with a
`.tar.gz` to carry to the server), and the rollback server's Docker image. `./build.sh TAG [CONTEXT]` does
the same with the image tagged as you say, and `./build.sh test`, `node`, `rendezvous`, `publish` or `image`
build one part. It needs the .NET 10 SDK, and Docker for the image. `./build.sh help` lists the forms.

## Code Style

This project uses TypeScript. Please keep your code consistent with the existing style. We use Prettier for formatting — please run it before submitting a PR.

## Questions

If you're unsure about anything, please open an issue or reach out in the community Discord server before spending time on a large change.
