# Client

This project was generated with [Angular CLI](https://github.com/angular/angular-cli) version 11.2.1.

> **Toolchain note:** install dependencies with `npm install --legacy-peer-deps` (required on npm 7+ to satisfy the Angular 11 peer-dependency graph). On Node 17+, the Angular 11 (webpack 4) CLI commands below — `ng serve`, `ng build`, `ng test` — require the `NODE_OPTIONS=--openssl-legacy-provider` environment prefix shown in each example.

## Development server

Run `NODE_OPTIONS=--openssl-legacy-provider ng serve` for a dev server. Navigate to `http://localhost:4200/`. The app will automatically reload if you change any of the source files.

## Code scaffolding

Run `ng generate component component-name` to generate a new component. You can also use `ng generate directive|pipe|service|class|guard|interface|enum|module`.

## Build

Run `NODE_OPTIONS=--openssl-legacy-provider ng build` to build the project. Use the `--prod` flag for a production build. Note: this project's `angular.json` sets `outputPath` to `../API/wwwroot`, so the build artifacts are emitted there (to be served by the API) rather than into a local `dist/` directory.

## Running unit tests

Run `NODE_OPTIONS=--openssl-legacy-provider ng test` to execute the unit tests via [Karma](https://karma-runner.github.io). In a headless/CI environment, set `CHROME_BIN=/usr/bin/google-chrome` and add `--watch=false --browsers=ChromeHeadlessNoSandbox`.

## Running end-to-end tests

Run `ng e2e` to execute the end-to-end tests via [Protractor](http://www.protractortest.org/).

## Further help

To get more help on the Angular CLI use `ng help` or go check out the [Angular CLI Overview and Command Reference](https://angular.io/cli) page.
