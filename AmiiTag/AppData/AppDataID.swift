//
//  AppDataID.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 4/6/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation

enum AppDataID: Int {
    case None = 0
    case AnimalCrossingHappyHomeDesigner = 0x0014F000
    case ChibiRoboZipLash = 0x00152600
    case LinksAwakening = 0x3B440400
    case MarioLuigiPaperJam = 0x00132600
    case SmashBros = 0x10110E00
    case SmashBrosUltimate = 0x34F80200
    case Splatoon2 = 0x10162B00
    case TwilightPrincessHD = 0x1019C800
    // Mario Party 10
}

extension AppDataID {
    var Name: String {
        switch self {
        case .None:
            return "No App Data"
            
        case .AnimalCrossingHappyHomeDesigner:
            return "Animal Crossing: Happy Home Designer"
            
        case .ChibiRoboZipLash:
            return "Chibi-Robo!: Zip-Lash"
            
        case .LinksAwakening:
            return "The Legend of Zelda: Link's Awakening"
            
        case .MarioLuigiPaperJam:
            return "Mario & Luigi: Paper Jam"
            
        case .SmashBros:
            return "Super Smash Bros"
            
        case .SmashBrosUltimate:
            return "Super Smash Bros: Ultimate"
            
        case .Splatoon2:
            return "Splatoon 2"
            
        case .TwilightPrincessHD:
            return "The Legend of Zelda: Twilight Princess HD"
        }
    }
}
