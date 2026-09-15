// Muralis.Core.Models.Monitor collides with System.Threading.Monitor (an implicit using). In this
// project the Core display model is what an unqualified "Monitor" means.
global using Monitor = Muralis.Core.Models.Monitor;
