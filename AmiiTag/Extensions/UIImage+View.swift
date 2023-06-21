//
//  UIImage+View.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 6/20/23.
//  Copyright © 2023 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

extension UIImage {
    convenience init?(view: UIView) {
        let renderer = UIGraphicsImageRenderer(bounds: view.bounds)
        let image = renderer.image { rendererContext in
            view.layer.render(in: rendererContext.cgContext)
        }

        if let cgImage = image.cgImage {
            self.init(cgImage: cgImage, scale: UIScreen.main.scale, orientation: .up)
        } else {
            return nil
        }
    }
}
