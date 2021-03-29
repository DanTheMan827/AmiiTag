//
//  TagDump+GetImage.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 3/25/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

extension TagDump {
    static func GetImage(id: String) -> UIImage? {
        if id.count != 16 && id.count != 18 {
            return nil
        }
        
        let fullHex = String(id.suffix(16))
        let headHex = fullHex.prefix(8)
        let tailHex = fullHex.suffix(8)
        
        var imageFilename = "icon_\(headHex)-\(tailHex)"
        if let realId = AmiiboDatabase.fakeAmiibo["\(headHex)\(tailHex)"] {
            imageFilename = "icon_\(realId.prefix(8))-\(realId.suffix(8))"
        }
        
        let imagePath = AmiiboDatabase.imagesPath.appendingPathComponent(imageFilename)
        
        if let image = UIImage(contentsOfFile: imagePath.path) {
            return image
        }
        
        return nil
    }
}
