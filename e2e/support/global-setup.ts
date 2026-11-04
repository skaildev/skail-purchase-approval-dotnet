import { build } from "./processes";

// builds the solution once, before the suite starts
export default function globalSetup() {
  build();
}
